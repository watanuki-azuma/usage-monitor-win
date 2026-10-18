//! Test fakes (port of the helpers in tests/Harness.cs and tests/M2_Adapters.cs / M3_GeminiGrok.cs):
//! `FakeHttp` records requests and answers with scripted replies, `FakeCred` serves credential
//! blobs, `AdapterEnv` is a temp "home" with an `AdapterContext`, `TestServer` is a tiny loopback
//! HTTP server for the `RealHttp` tests (loopback only, no real network).
use std::collections::HashMap;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;

use chrono::{DateTime, TimeZone, Utc};

use crate::core::app_config::AppConfig;
use crate::core::contracts::{AdapterContext, CredentialReader, Http, HttpError, HttpResult};
use crate::core::paths::Paths;

pub fn utc(y: i32, mo: u32, d: u32, h: u32, mi: u32, s: u32) -> DateTime<Utc> {
    Utc.with_ymd_and_hms(y, mo, d, h, mi, s).unwrap()
}

pub fn json(text: &str) -> serde_json::Value {
    serde_json::from_str(text).unwrap()
}

/// Assert.Equal for doubles: tolerance 1e-9.
pub fn assert_f64(expected: f64, actual: Option<f64>, what: &str) {
    match actual {
        Some(a) if (expected - a).abs() < 1e-9 => {}
        other => panic!("{}: expected {} but was {:?}", what, expected, other),
    }
}

pub fn assert_contains(haystack: &str, needle: &str, what: &str) {
    assert!(haystack.contains(needle), "{}: expected {:?} to contain {:?}", what, haystack, needle);
}

#[derive(Clone, Debug)]
pub struct RecordedRequest {
    pub method: String,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<String>,
}

impl RecordedRequest {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }
}

pub type HandlerResult = Result<HttpResult, HttpError>;
type Handler = Arc<dyn Fn(&RecordedRequest) -> HandlerResult + Send + Sync>;

#[derive(Default)]
pub struct FakeHttp {
    requests: Mutex<Vec<RecordedRequest>>,
    handler: Mutex<Option<Handler>>,
}

impl FakeHttp {
    pub fn set_handler<F>(&self, f: F)
    where
        F: Fn(&RecordedRequest) -> HandlerResult + Send + Sync + 'static,
    {
        *self.handler.lock().unwrap() = Some(Arc::new(f));
    }

    pub fn requests(&self) -> Vec<RecordedRequest> {
        self.requests.lock().unwrap().clone()
    }

    pub fn count(&self) -> usize {
        self.requests.lock().unwrap().len()
    }
}

impl Http for FakeHttp {
    fn request(
        &self,
        method: &str,
        url: &str,
        headers: &[(&str, &str)],
        body: Option<&[u8]>,
        _timeout_ms: u32,
    ) -> Result<HttpResult, HttpError> {
        let mut recorded = RecordedRequest {
            method: method.to_string(),
            url: url.to_string(),
            headers: Vec::new(),
            body: body.map(|b| String::from_utf8_lossy(b).into_owned()),
        };
        for (k, v) in headers {
            match recorded.headers.iter_mut().find(|(rk, _)| rk == k) {
                Some(slot) => slot.1 = v.to_string(),
                None => recorded.headers.push((k.to_string(), v.to_string())),
            }
        }
        self.requests.lock().unwrap().push(recorded.clone());
        let handler = self.handler.lock().unwrap().clone();
        match handler {
            None => Err(HttpError(format!("unexpected HTTP request to {}", url))),
            Some(h) => h(&recorded),
        }
    }
}

/// FakeHttp.Reply(status, json)
pub fn reply(status: u16, text: &str) -> HandlerResult {
    Ok(HttpResult {
        status,
        text: text.to_string(),
        raw: text.as_bytes().to_vec(),
        headers: HashMap::new(),
        json: None,
    })
}

/// A reply with a binary body (grpc-web).
pub fn binary(status: u16, bytes: Vec<u8>) -> HandlerResult {
    Ok(HttpResult { status, text: String::new(), raw: bytes, headers: HashMap::new(), json: None })
}

#[derive(Default)]
pub struct FakeCred {
    pub blobs: Mutex<HashMap<String, String>>,
    pub reads: Mutex<Vec<String>>,
}

impl FakeCred {
    pub fn set(&self, target: &str, blob: &str) {
        self.blobs.lock().unwrap().insert(target.to_string(), blob.to_string());
    }

    pub fn reads(&self) -> Vec<String> {
        self.reads.lock().unwrap().clone()
    }
}

impl CredentialReader for FakeCred {
    fn read_generic_utf8(&self, target: &str) -> Option<String> {
        self.reads.lock().unwrap().push(target.to_string());
        self.blobs.lock().unwrap().get(target).cloned()
    }
}

static COUNTER: AtomicUsize = AtomicUsize::new(0);

pub struct AdapterEnv {
    pub home: PathBuf,
    pub bin_dir: PathBuf,
    pub http: Arc<FakeHttp>,
    pub config: AppConfig,
    pub now: DateTime<Utc>,
    pub credentials: Arc<FakeCred>,
}

impl AdapterEnv {
    pub fn new() -> AdapterEnv {
        let unique = format!(
            "um-test-{}-{}-{}",
            std::process::id(),
            COUNTER.fetch_add(1, Ordering::SeqCst),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0)
        );
        let home = std::env::temp_dir().join(unique);
        std::fs::create_dir_all(&home).unwrap();
        let bin_dir = home.join("bin");
        std::fs::create_dir_all(&bin_dir).unwrap();
        AdapterEnv {
            home,
            bin_dir,
            http: Arc::new(FakeHttp::default()),
            config: AppConfig::default(),
            now: utc(2026, 10, 11, 12, 0, 0),
            credentials: Arc::new(FakeCred::default()),
        }
    }

    pub fn context(&self, cli_on_path: bool, cli_name: &str) -> AdapterContext {
        if cli_on_path {
            std::fs::write(self.bin_dir.join(format!("{}.cmd", cli_name)), "@echo off").unwrap();
        }
        let mut env: HashMap<String, String> = HashMap::new();
        env.insert("USERPROFILE".into(), self.home.to_string_lossy().into_owned());
        env.insert("APPDATA".into(), self.home.join("AppData").join("Roaming").to_string_lossy().into_owned());
        env.insert("LOCALAPPDATA".into(), self.home.join("AppData").join("Local").to_string_lossy().into_owned());
        env.insert("PATH".into(), self.bin_dir.to_string_lossy().into_owned());
        let paths = Paths::new(move |k| env.get(k).cloned());
        let now = self.now;
        AdapterContext {
            http: self.http.clone(),
            paths,
            credentials: self.credentials.clone(),
            config: self.config.clone(),
            utc_now: Arc::new(move || now),
        }
    }

    /// Writes `content` (UTF-8, no BOM) below the temp home and returns the full path.
    pub fn write(&self, relative: &str, content: &str) -> String {
        let p = self.home.join(relative);
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(&p, content.as_bytes()).unwrap();
        p.to_string_lossy().into_owned()
    }
}

impl Drop for AdapterEnv {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.home);
    }
}

// ----------------------------------------------------------------------------------------------
// A tiny loopback HTTP server for the RealHttp tests.
// ----------------------------------------------------------------------------------------------

pub struct ServerRequest {
    pub method: String,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl ServerRequest {
    pub fn header(&self, name: &str) -> Option<&str> {
        self.headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
    }
}

pub struct TestServer {
    pub base_url: String,
    stop: Arc<AtomicBool>,
    port: u16,
}

impl TestServer {
    /// `handler` writes the whole raw HTTP response (use `respond`) to the stream.
    pub fn new<F>(handler: F) -> TestServer
    where
        F: Fn(&ServerRequest, &mut TcpStream) + Send + Sync + 'static,
    {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        let stop = Arc::new(AtomicBool::new(false));
        let handler = Arc::new(handler);
        let stop_flag = stop.clone();
        thread::spawn(move || {
            for stream in listener.incoming() {
                if stop_flag.load(Ordering::SeqCst) {
                    return;
                }
                let mut stream = match stream {
                    Ok(s) => s,
                    Err(_) => return,
                };
                let handler = handler.clone();
                thread::spawn(move || {
                    if let Some(req) = read_request(&mut stream) {
                        handler(&req, &mut stream);
                    }
                    let _ = stream.flush();
                });
            }
        });
        TestServer { base_url: format!("http://127.0.0.1:{}/", port), stop, port }
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        let _ = TcpStream::connect(("127.0.0.1", self.port)); // wake the accept loop
    }
}

fn read_request(stream: &mut TcpStream) -> Option<ServerRequest> {
    let mut data: Vec<u8> = Vec::new();
    let mut chunk = [0u8; 4096];
    let header_end = loop {
        if let Some(pos) = data.windows(4).position(|w| w == b"\r\n\r\n") {
            break pos;
        }
        let n = stream.read(&mut chunk).ok()?;
        if n == 0 {
            return None;
        }
        data.extend_from_slice(&chunk[..n]);
    };
    let head = String::from_utf8_lossy(&data[..header_end]).into_owned();
    let mut lines = head.split("\r\n");
    let request_line = lines.next()?;
    let method = request_line.split(' ').next()?.to_string();
    let mut headers = Vec::new();
    for line in lines {
        if let Some((k, v)) = line.split_once(':') {
            headers.push((k.trim().to_string(), v.trim().to_string()));
        }
    }
    let content_length = headers
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case("content-length"))
        .and_then(|(_, v)| v.parse::<usize>().ok())
        .unwrap_or(0);
    let mut body: Vec<u8> = data[header_end + 4..].to_vec();
    while body.len() < content_length {
        let n = stream.read(&mut chunk).ok()?;
        if n == 0 {
            break;
        }
        body.extend_from_slice(&chunk[..n]);
    }
    Some(ServerRequest { method, headers, body })
}

/// Writes a complete response with a Content-Length body and `Connection: close`.
pub fn respond(stream: &mut TcpStream, status: u16, extra_headers: &[(&str, &str)], body: &[u8]) {
    let mut head = format!("HTTP/1.1 {} X\r\nContent-Length: {}\r\nConnection: close\r\n", status, body.len());
    for (k, v) in extra_headers {
        head.push_str(&format!("{}: {}\r\n", k, v));
    }
    head.push_str("\r\n");
    let _ = stream.write_all(head.as_bytes());
    let _ = stream.write_all(body);
}

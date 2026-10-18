//! Headless software rendering; never creates an OS window or reads credentials.
use std::{cell::RefCell, path::Path, rc::Rc};
use chrono::{DateTime, Local, Utc};
use slint::{Model, ModelRc, VecModel, Rgb8Pixel};
use slint::platform::{Platform, WindowAdapter, software_renderer::{MinimalSoftwareWindow, RepaintBufferType}};
use crate::core::{contracts::{Snapshot, SnapshotStore, state, kind}, format};
use crate::engine::stores::FileSnapshotStore;
use super::{Chips, Flyout, ChipData, CardData, LineData, chip_model};

fn color(rgb: u32) -> slint::Color { slint::Color::from_rgb_u8((rgb>>16) as u8,(rgb>>8) as u8,rgb as u8) }
fn brand(id: &str) -> (&str,u32) {
    match id { "claude" => ("C",0xe07a5f), "codex" => ("X",0x60a5fa), "gemini" => ("G",0x7c9cff), "grok" => ("K",0xd6d3d1), _ => ("?",0x9ca3af) }
}
fn dashed_outline(width:f32)->String {
    use std::f32::consts::{PI,FRAC_PI_2};
    let (r,h)=(5.,22.);let a=width-2.*r-1.;let b=FRAC_PI_2*r;let c=h-2.*r-1.;
    let spans=[a,b,c,b,a,b,c,b];
    let point=|mut distance:f32| {
        let mut side=7;
        for (i,span) in spans.iter().enumerate(){side=i;if distance<=*span{break;}distance-=span;}
        match side {
            0=>(r+0.5+distance,0.5),
            1=>{let angle=-FRAC_PI_2+distance/r;(width-r-0.5+r*angle.cos(),r+0.5+r*angle.sin())},
            2=>(width-0.5,r+0.5+distance),
            3=>{let angle=distance/r;(width-r-0.5+r*angle.cos(),h-r-0.5+r*angle.sin())},
            4=>(width-r-0.5-distance,h-0.5),
            5=>{let angle=FRAC_PI_2+distance/r;(r+0.5+r*angle.cos(),h-r-0.5+r*angle.sin())},
            6=>(0.5,h-r-0.5-distance),
            _=>{let angle=PI+distance/r;(r+0.5+r*angle.cos(),r+0.5+r*angle.sin())},
        }
    };
    let perimeter: f32=spans.iter().sum();let mut path=String::new();let mut d=0.;
    use std::fmt::Write;
    while d<perimeter {if (d as i32/3)%2==0 {let (x,y)=point(d);let (nx,ny)=point((d+1.).min(perimeter));let _=write!(path,"M{x:.2} {y:.2} L{nx:.2} {ny:.2} ");}d+=1.;}
    path
}
// GDI measures Segoe UI exactly as Theme.TextWidth (NoPadding), without a window.
pub fn text_width(text: &str, px: i32, bold: bool) -> i32 {
    if text.is_empty() {return 0;}
    #[cfg(windows)] unsafe {
        use windows_sys::Win32::{Graphics::Gdi::*, Foundation::SIZE};
        let dc=CreateCompatibleDC(std::ptr::null_mut());
        let font=CreateFontW(-px,0,0,0,if bold {700} else {400},0,0,0,1,0,0,0,0,
            "Segoe UI\0".encode_utf16().collect::<Vec<_>>().as_ptr());
        let old=SelectObject(dc,font);
        let wide: Vec<_>=text.encode_utf16().collect();
        let mut size: SIZE=std::mem::zeroed();
        GetTextExtentPoint32W(dc,wide.as_ptr(),wide.len() as i32,&mut size);
        SelectObject(dc,old); DeleteObject(font); DeleteDC(dc);
        // Theme.TextWidth uses TextRenderer.MeasureText: on this .NET/GDI path it retains
        // seven pixels of overhang even with NoPadding (verified against the C# method).
        size.cx + 7
    }
    #[cfg(not(windows))] { (text.len() as i32 * px * 6)/10 }
}
pub fn update(chips: &Chips, flyout: &Flyout, snapshot: Option<&Snapshot>, now: DateTime<Utc>) {
    let mut x=7;
    let mut chip_data=Vec::new();
    if let Some(s)=snapshot {
        for p in &s.providers {
            let c=chip_model::for_provider(Some(p)); let (initial,accent)=brand(&p.id);
            let w=31+text_width(&c.text,11,true);
            let (ink,outline)=match c.color_class { "red" => (0xfecaca,0xef4444), "green" => (0xbbf7d0,0x22c55e), _ => (0x9ca3af,0x6b7280) };
            chip_data.push(ChipData { id:p.id.clone().into(), text:c.text.into(), initial:initial.into(), accent:color(accent), ink:color(ink), outline:color(outline), stale:c.stale, px:x as f32, pw:w as f32, dash_path:if c.stale {dashed_outline(w as f32).into()}else{Default::default()} });
            x+=w+6;
        }
    }
    if chip_data.is_empty() {
        let w=31+text_width("loading…",11,true);
        chip_data.push(ChipData { text:"loading…".into(), initial:"?".into(), ink:color(0x9ca3af), outline:color(0x6b7280), accent:color(0x9ca3af), px:7.,pw:w as f32, ..Default::default() }); x=7+w+6;
    }
    let width=(x+1).clamp(96,720) as f32;
    let model:ModelRc<ChipData>=Rc::new(VecModel::from(chip_data)).into();
    // Updating equal properties does not invalidate Slint's scene.
    if chips.get_chips().iter().collect::<Vec<_>>() != model.iter().collect::<Vec<_>>() { chips.set_chips(model); }
    chips.set_content_width(width); chips.set_content_height(30.);

    let mut cards=Vec::new(); let mut heights=Vec::new();
    if let Some(s)=snapshot { for (index,p) in s.providers.iter().enumerate() {
        let (initial,accent)=brand(&p.id); let mut lines=Vec::new(); let mut y=30.; let mut label_width=28;
        let mut add=|mut line:LineData| { line.py=y; y+=if line.kind==0 {22.} else {20.}; lines.push(line); };
        if p.status.state==state::STALE { add(LineData { kind:4,text:format::stale_text(p,now,&Local).into(),..Default::default() }); }
        for w in &p.windows {
            if w.kind==kind::CREDITS && w.used_pct.is_none() { add(LineData { kind:1,text:w.label.clone().into(),..Default::default() }); continue; }
            let label=format::flyout_label(w); label_width=label_width.max(text_width(&label,11,true)+2);
            add(LineData { kind:0,label:label.into(),pct:format::format_used_total(Some(w),false).into(),eta:format::format_eta(w.resets_at,now,&Local).into(),fill:w.used_pct.unwrap_or(0.).clamp(0.,100.) as f32,alert:crate::core::models::is_alerting(w),..Default::default() });
        }
        for note in p.footnotes.iter().filter(|n| !n.is_empty()) { add(LineData {kind:3,text:note.clone().into(),..Default::default()}); }
        drop(add);
        if lines.is_empty() { if let Some(hint)=format::status_text(p) { lines.push(LineData {kind:2,text:hint.into(),py:y,..Default::default()}); y+=20.; } }
        let h=if lines.is_empty() {39.} else {y-6.+9.}; heights.push(h);
        for line in &mut lines {line.pct_width=text_width(line.pct.as_str(),11,true).min(46) as f32;}
        cards.push(CardData {id:p.id.clone().into(),name:p.display_name.as_ref().unwrap_or(&p.id).clone().into(),plan:p.plan.clone().unwrap_or_default().into(),initial:initial.into(),accent:color(accent),lines:Rc::new(VecModel::from(lines)).into(),px:if index%2==0 {13.} else {324.},py:0.,ph:0.,label_width:label_width.min(104) as f32,eta_width:46.});
    } }
    let eta_width=cards.iter().flat_map(|c|c.lines.iter()).filter(|l|l.kind==0&&!l.eta.is_empty()).map(|l|text_width(l.eta.as_str(),11,false)+2).max().unwrap_or(46).max(46) as f32;
    for c in &mut cards {c.eta_width=eta_width;c.label_width=c.label_width.min(279.-46.-eta_width-48.);if c.label_width<28.{c.label_width=69.;}}
    let mut y=49.;
    for row in (0..cards.len()).step_by(2) { let h=heights[row].max(*heights.get(row+1).unwrap_or(&40.)); for c in cards.iter_mut().skip(row).take(2) {c.py=y;c.ph=h;} y+=h+8.; }
    flyout.set_content_width(640.); flyout.set_content_height(if cards.is_empty() {120.} else {y+3.});
    // Nested models compare by identity; use a visible-value key to avoid idle repaints.
    let key=cards.iter().map(|c|format!("{:?}",(&c.id,&c.name,&c.plan,&c.initial,c.accent,c.px,c.py,c.ph,c.label_width,c.eta_width,c.lines.iter().collect::<Vec<_>>()))).collect::<Vec<_>>().join("|");
    if flyout.get_render_key().as_str()!=key { flyout.set_render_key(key.into()); flyout.set_cards(Rc::new(VecModel::from(cards)).into()); }
}

struct Headless { windows:Rc<RefCell<Vec<Rc<MinimalSoftwareWindow>>>> }
impl Platform for Headless {
    fn create_window_adapter(&self)->Result<Rc<dyn WindowAdapter>,slint::PlatformError> {
        let w=MinimalSoftwareWindow::new(RepaintBufferType::NewBuffer);
        self.windows.borrow_mut().push(w.clone()); Ok(w)
    }
}
pub fn install_headless() -> Result<Rc<RefCell<Vec<Rc<MinimalSoftwareWindow>>>>,slint::PlatformError> {
    let windows=Rc::new(RefCell::new(Vec::new()));
    slint::platform::set_platform(Box::new(Headless {windows:windows.clone()})).map_err(|e|slint::PlatformError::from(e.to_string()))?; Ok(windows)
}
pub fn physical_size(w:f32,h:f32,scale:f32)->slint::PhysicalSize {
    slint::PhysicalSize::new((w*scale).round() as u32,(h*scale).round() as u32)
}
pub fn pixels(window:&MinimalSoftwareWindow,w:u32,h:u32,scale:f32) -> Vec<u8> {
    window.dispatch_event(slint::platform::WindowEvent::ScaleFactorChanged {scale_factor:scale});
    window.set_size(slint::PhysicalSize::new(w,h));
    let mut pixels=vec![Rgb8Pixel::default(); (w*h) as usize];
    // render() directly: no OS adapter, no show(), no event loop.
    window.request_redraw();
    window.draw_if_needed(|renderer| { renderer.render(&mut pixels,w as usize); });
    pixels.into_iter().flat_map(|p| [p.r,p.g,p.b]).collect()
}
pub fn load_snapshot(data_dir:&Path,exe_dir:&Path)->Option<Snapshot> {
    FileSnapshotStore::new(data_dir.join("snapshot.json")).load().or_else(|| exe_dir.ancestors().find_map(|p| FileSnapshotStore::new(p.join("fixtures/snapshot-sample.json")).load()))
}
pub fn run(out:&Path,data:&Path,exe:&Path,_skin:Option<&str>)->Result<(),Box<dyn std::error::Error>> {
    if out.as_os_str().is_empty(){return Err(std::io::Error::new(std::io::ErrorKind::InvalidInput,"output directory is required").into());}
    std::fs::create_dir_all(out)?;
    let windows=install_headless()?; let chips=Chips::new()?; let flyout=Flyout::new()?;
    update(&chips,&flyout,load_snapshot(data,exe).as_ref(),Utc::now());
    #[cfg(windows)] let scale=crate::win::dpi::scale();
    #[cfg(not(windows))] let scale=1.;
    for (index,name,width,height) in [(0,"chips.png",chips.get_content_width(),30.),(1,"flyout.png",640.,flyout.get_content_height())] {
        let slint::PhysicalSize {width:w,height:h}=physical_size(width,height,scale);
        let data=pixels(&windows.borrow()[index],w,h,scale);
        let mut encoder=png::Encoder::new(std::fs::File::create(out.join(name))?,w,h);
        encoder.set_color(png::ColorType::Rgb); encoder.set_depth(png::BitDepth::Eight);
        encoder.write_header()?.write_image_data(&data)?;
    }
    Ok(())
}

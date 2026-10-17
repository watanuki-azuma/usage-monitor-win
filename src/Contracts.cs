// Shared data types and interfaces. Written by the project owner; implementers must NOT edit this file.
// Language level is C# 5 (in-box csc.exe): no ?. , no $"" , no nameof, no expression-bodied members,
// no auto-property initializers, no out var, no tuples.
using System;
using System.Collections.Generic;

namespace UsageMonitor
{
    public static class Kind
    {
        public const string FiveHour = "five_hour";
        public const string Weekly = "weekly";
        public const string WeeklyScoped = "weekly_scoped";
        public const string Daily = "daily";
        public const string Monthly = "monthly";
        public const string Quota = "quota";
        public const string Credits = "credits";
    }

    public static class State
    {
        public const string Ok = "ok";
        public const string Stale = "stale";
        public const string LoggedOut = "logged_out";
        public const string NotInstalled = "not_installed";
        public const string FetchFailed = "fetch_failed";
        public const string Unknown = "unknown";
    }

    public class UsageWindow
    {
        public string Kind { get; set; }
        public string Label { get; set; }
        public double? UsedPct { get; set; }       // null = unknown
        public double? RemainingPct { get; set; }  // max(0, 100 - UsedPct); null when UsedPct is null
        public DateTime? ResetsAt { get; set; }    // always DateTimeKind.Utc when set

        public UsageWindow Clone()
        {
            UsageWindow w = new UsageWindow();
            w.Kind = Kind;
            w.Label = Label;
            w.UsedPct = UsedPct;
            w.RemainingPct = RemainingPct;
            w.ResetsAt = ResetsAt;
            return w;
        }
    }

    public class ProviderStatus
    {
        public string State { get; set; }      // one of UsageMonitor.State.*
        public string Hint { get; set; }       // logged_out / not_installed
        public string Message { get; set; }    // fetch_failed / unknown
        public string Reason { get; set; }     // "rate_limited" when backing off
        public DateTime? RetryAt { get; set; } // UTC
        public int? AgeSecs { get; set; }      // stale only
    }

    public class ProviderSnapshot
    {
        public string Id { get; set; }          // claude | codex | gemini | grok
        public string DisplayName { get; set; }
        public ProviderStatus Status { get; set; }
        public string Plan { get; set; }
        public List<UsageWindow> Windows { get; set; }
        public List<string> Footnotes { get; set; }  // may be empty, never null after parsing
        public DateTime FetchedAt { get; set; }      // UTC
        public string Source { get; set; }           // "api" | "cache" | "none"
        public bool RateLimited { get; set; }        // in-memory only (JS: _rateLimited); NOT serialized

        public ProviderSnapshot()
        {
            Windows = new List<UsageWindow>();
            Footnotes = new List<string>();
        }
    }

    public class Snapshot
    {
        public DateTime GeneratedAt { get; set; }    // UTC
        public int PollIntervalSecs { get; set; }
        public List<ProviderSnapshot> Providers { get; set; }

        public Snapshot()
        {
            Providers = new List<ProviderSnapshot>();
        }
    }

    // ---- HTTP -------------------------------------------------------------------------------

    public class HttpResult
    {
        public int Status { get; set; }                       // 0 is never returned; transport errors throw
        public string Text { get; set; }                      // UTF-8 decoded body ("" when empty)
        public byte[] Raw { get; set; }                       // raw body bytes
        public Dictionary<string, string> Headers { get; set; } // keys lower-cased
        public object Json { get; set; }                      // parsed body (JavaScriptSerializer.DeserializeObject) or null when not JSON
    }

    public interface IHttp
    {
        // Throws on transport error, timeout, unsupported scheme (only http/https), or body > 2 MiB.
        HttpResult Request(string method, string url, IDictionary<string, string> headers, byte[] body, int timeoutMs);
    }

    public interface ICredentialReader
    {
        // Reads a Windows generic credential blob and returns it decoded as UTF-8, or null when absent.
        string ReadGenericUtf8(string target);
    }

    // ---- Adapters ---------------------------------------------------------------------------

    public class AdapterContext
    {
        public IHttp Http { get; set; }
        public Paths Paths { get; set; }
        public ICredentialReader Credentials { get; set; }
        public AppConfig Config { get; set; }
        public Func<DateTime> UtcNow { get; set; }
    }

    public interface IUsageAdapter
    {
        string Id { get; }
        string DisplayName { get; }
        // Blocking. Never throws for expected failures (returns a provider with State fetch_failed / logged_out ...).
        ProviderSnapshot FetchUsage(AdapterContext ctx);
    }

    // ---- Persistence ------------------------------------------------------------------------

    public interface ISnapshotStore
    {
        Snapshot Load();               // null when missing/invalid
        void Save(Snapshot snapshot);  // atomic (tmp + replace); may throw
    }

    public class AlertEntry
    {
        public bool Crossed80 { get; set; }
        public bool Crossed100 { get; set; }
        public DateTime? Until { get; set; }   // UTC; null = keep until pruned never
    }

    public class AlertState
    {
        public Dictionary<string, AlertEntry> Fired { get; set; }
        public AlertState()
        {
            Fired = new Dictionary<string, AlertEntry>();
        }
    }

    public class Toast
    {
        public string Title { get; set; }
        public string Body { get; set; }
    }
}

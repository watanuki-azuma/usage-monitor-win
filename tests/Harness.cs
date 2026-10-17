// Minimal test harness (owner-written). Test methods: public static void Test_*() in classes named *Tests.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace UsageMonitor.Tests
{
    public class AssertionException : Exception
    {
        public AssertionException(string message) : base(message) { }
    }

    public static class Assert
    {
        static string Show(object v)
        {
            if (v == null) return "null";
            if (v is double) return ((double)v).ToString("R", CultureInfo.InvariantCulture);
            if (v is DateTime) return ((DateTime)v).ToString("o", CultureInfo.InvariantCulture) + "(" + ((DateTime)v).Kind + ")";
            return "\"" + Convert.ToString(v, CultureInfo.InvariantCulture) + "\" (" + v.GetType().Name + ")";
        }

        public static void Equal(object expected, object actual, string what)
        {
            if (expected is double && actual is double && Math.Abs((double)expected - (double)actual) < 1e-9) return;
            if (!object.Equals(expected, actual))
                throw new AssertionException(what + ": expected " + Show(expected) + " but was " + Show(actual));
        }

        public static void True(bool cond, string what)
        {
            if (!cond) throw new AssertionException(what + ": expected true");
        }

        public static void False(bool cond, string what)
        {
            if (cond) throw new AssertionException(what + ": expected false");
        }

        public static void Null(object v, string what)
        {
            if (v != null) throw new AssertionException(what + ": expected null but was " + Show(v));
        }

        public static void NotNull(object v, string what)
        {
            if (v == null) throw new AssertionException(what + ": expected non-null");
        }

        public static void Throws(Action a, string what)
        {
            try { a(); }
            catch (Exception) { return; }
            throw new AssertionException(what + ": expected an exception");
        }

        public static void Contains(string haystack, string needle, string what)
        {
            if (haystack == null || haystack.IndexOf(needle, StringComparison.Ordinal) < 0)
                throw new AssertionException(what + ": expected " + Show(haystack) + " to contain \"" + needle + "\"");
        }
    }

    public class JsonHelper
    {
        public object Parse(string json)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json);
        }
    }

    public static class Dates
    {
        public static DateTime Utc(int y, int m, int d, int h, int min, int s)
        {
            return new DateTime(y, m, d, h, min, s, DateTimeKind.Utc);
        }
    }

    public static class TempDir
    {
        public static string Create()
        {
            string p = Path.Combine(Path.GetTempPath(), "um-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(p);
            return p;
        }
    }

    public static class Runner
    {
        public static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : null;
            int passed = 0, failed = 0;
            Assembly asm = typeof(Runner).Assembly;
            List<Type> types = asm.GetTypes()
                .Where(t => t.Namespace == "UsageMonitor.Tests" && t.Name.EndsWith("Tests") && t.IsClass)
                .OrderBy(t => t.Name).ToList();
            foreach (Type t in types)
            {
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).OrderBy(x => x.Name))
                {
                    if (!m.Name.StartsWith("Test_")) continue;
                    string name = t.Name + "." + m.Name;
                    if (filter != null && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        m.Invoke(null, null);
                        passed++;
                        Console.WriteLine("PASS " + name);
                    }
                    catch (TargetInvocationException ex)
                    {
                        failed++;
                        Exception inner = ex.InnerException ?? ex;
                        Console.WriteLine("FAIL " + name + ": " + inner.GetType().Name + ": " + inner.Message);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}

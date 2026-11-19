// A stand-in for Murmur.Cleanup.exe in the LocalCleanupRunner tests: speaks the line protocol with canned behavior chosen by the request text.
//   argv[0] is the "model folder"; the process id is written to <folder>/pid so tests can see which child served a request.
//   {"ready":true} first, then one reply per request line:
//   text contains "toolong" -> {"id","error":"too-long"}     "broken" -> {"id","error":"failed"}     "hang" -> no reply (sleeps 30 s)
//   "slow" -> reply after 1 s     "wrongid" -> reply with another id     "garbage" -> a non-JSON line     "echo" -> reply with the request's tone and terms
//   anything else -> {"id","text":"reply <pid>"}.  A file <folder>/notready makes it print {"ready":false} and exit 3.
using System.Diagnostics;
using System.Text.Json;

var folder = args.Length > 0 ? args[0] : ".";
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
File.WriteAllText(Path.Combine(folder, "pid"), Environment.ProcessId.ToString());
if (File.Exists(Path.Combine(folder, "notready"))) { Console.Out.WriteLine("{\"ready\":false}"); return 3; }
Console.Out.WriteLine("{\"ready\":true}");
Console.Out.Flush();

string? line;
while ((line = Console.In.ReadLine()) is not null)
{
    using var request = JsonDocument.Parse(line);
    var id = request.RootElement.GetProperty("id").GetInt32();
    var text = request.RootElement.GetProperty("text").GetString() ?? "";
    string reply;
    if (text.Contains("toolong")) reply = JsonSerializer.Serialize(new { id, error = "too-long" });
    else if (text.Contains("broken")) reply = JsonSerializer.Serialize(new { id, error = "failed" });
    else if (text.Contains("hang")) { Thread.Sleep(30_000); continue; }
    else if (text.Contains("garbage")) reply = "not json";
    else if (text.Contains("wrongid")) reply = JsonSerializer.Serialize(new { id = id + 100, text = "x" });
    else if (text.Contains("echo")) reply = JsonSerializer.Serialize(new { id, text = $"{request.RootElement.GetProperty("tone").GetString()}|{string.Join(",", request.RootElement.GetProperty("terms").EnumerateArray().Select(t => t.GetString()))}|{text}" });
    else
    {
        if (text.Contains("slow")) Thread.Sleep(1000);
        reply = JsonSerializer.Serialize(new { id, text = $"reply {Environment.ProcessId}" });
    }
    Console.Out.WriteLine(reply);
    Console.Out.Flush();
}
return 0;

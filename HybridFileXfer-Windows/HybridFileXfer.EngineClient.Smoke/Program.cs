using HybridFileXfer.EngineClient;

var javaPath = args.Length > 0 ? args[0] : "java";
var jarPath = args.Length > 1
    ? args[1]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
        "..", "HybridFileXfer-PC", "build", "HybridFileXfer.jar"));

if (!File.Exists(jarPath))
{
    Console.Error.WriteLine($"Jar not found: {jarPath}");
    return 2;
}

Console.WriteLine($"Java: {javaPath}");
Console.WriteLine($"Jar : {jarPath}");

await using var engine = await EngineClient.StartAsync(javaPath, jarPath, AppContext.BaseDirectory);
engine.EventReceived += e => Console.WriteLine($"[{e.Type}] {e.RawJson}");
engine.StandardError += line => Console.WriteLine($"[stderr] {line}");

var ping = await engine.PingAsync();
Console.WriteLine($"PING success={ping.Success}");

var shutdown = await engine.ShutdownAsync();
Console.WriteLine($"SHUTDOWN success={shutdown.Success}");

return ping.Success == true && shutdown.Success == true ? 0 : 1;

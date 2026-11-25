using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeviceHaven.Shared;

namespace DeviceHaven.Tray;

/// <summary>Asks the agent service (on this PC only) for its status.</summary>
internal static class AgentPipeClient
{
    public static TrayReply? Send(TrayRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", TrayPipe.Name, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, TrayJson.Default.TrayRequest) + "\n");
            pipe.Write(bytes);
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var line = reader.ReadLine();
            return line is null ? null : JsonSerializer.Deserialize(line, TrayJson.Default.TrayReply);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            return null;   // the agent isn't running or is restarting
        }
    }
}

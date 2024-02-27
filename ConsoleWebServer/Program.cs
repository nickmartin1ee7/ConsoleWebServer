using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

var port = 80;
var hostingDirectory = new FileInfo(Assembly.GetEntryAssembly()!.Location).DirectoryName!;
var permittedResourceDirectories = new string[]
{
    hostingDirectory
};

var portOption = new Option<int>(
    aliases: ["--port", "-p"],
    description: "The port that will accept TCP (HTTP) traffic.");

var permittedDirectoriesOption = new Option<string[]>(
    aliases: ["--dirs", "-d"],
    description: "The permitted directories that can be served to HTTP requests.");

var rootCommand = new Command(name: "run", description: "Privative Console Web Server");

rootCommand.AddOption(portOption);
rootCommand.AddOption(permittedDirectoriesOption);

rootCommand.SetHandler(
    (userPort, userDirs) =>
    {
        port = userPort;
        permittedResourceDirectories = userDirs;
    },
    portOption,
    permittedDirectoriesOption);

Console.WriteLine("Warming up...");

var allowedResourceDirectories = ScanAllowedResourceDirectories(permittedResourceDirectories);
var clients = new Dictionary<EndPoint, Socket>();
EventHandler<Socket> NewClientConnected = HandleNewClient;

try
{
    var listener = new TcpListener(localaddr: IPAddress.Any, port: port);
    listener.Start();

    Console.WriteLine($"Ready to accept clients on port: {port}");

    await ListenerLoop(listener);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to start! {ex}");
}

async Task ListenerLoop(TcpListener listener)
{
    while (true)
    {
        var socket = await listener.AcceptSocketAsync();
        var remoteEndpoint = socket.RemoteEndPoint;

        if (remoteEndpoint is not null)
        {
            clients.Add(socket.RemoteEndPoint!, socket);
            NewClientConnected?.Invoke(listener, socket);
        }
        else
        {
            Console.WriteLine("Failed to accept socket. No remote endpoint.");
        }
    }
}

async void HandleNewClient(object? sender, Socket socket)
{
    try
    {
        Console.WriteLine($"New client connected: {socket.RemoteEndPoint}");

        var message = await ReadData(socket, Encoding.UTF8);
        await TryHandleHttpRequest(message, socket);

        Console.WriteLine($"Client {socket.RemoteEndPoint} -> Server: {message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to handle new client ({socket.RemoteEndPoint})! {ex}");
    }
    finally
    {
        socket.Close();
    }
}

async Task<string> ReadData(Socket socket, Encoding encoding)
{
    var buffer = new byte[1024];
    _ = await socket.ReceiveAsync(buffer);
    var message = encoding.GetString(buffer);
    return message.Trim();
}

Task TryHandleHttpRequest(string message, Socket socket)
{
    if (message.Length == 0)
    {
        return Task.CompletedTask;
    }

    var lines = message.Split('\n');

    if (lines.Length <= 0)
    {
        return Task.CompletedTask;
    }

    var httpRequestLine = lines[0];
    var splitHttpRequestLine = httpRequestLine.Split(' ');

    /*
     * [0] - HTTP Method (GET)
     * [1] - Resource Path (/)
     * [2] - HTTP Version (HTTP/1.1)
     */
    if (splitHttpRequestLine.Length < 3)
    {
        return Task.CompletedTask;
    }

    var method = splitHttpRequestLine[0].Trim();
    var resourceLocator = splitHttpRequestLine[1].Trim();
    var httpVersion = splitHttpRequestLine[2].Trim();

    switch (method.ToUpperInvariant())
    {
        case Constants.HttpMethodGet:
            return HandleHttpGetRequest(resourceLocator, httpVersion, splitHttpRequestLine, socket);
        default:
            break;
    }

    return Task.CompletedTask;
}

async Task HandleHttpGetRequest(string resourceLocator, string httpVersion, string[] splitHttpRequestLine, Socket socket)
{
    var httpResponse = new StringBuilder(httpVersion + " ");

    try
    {
        var queryIndex = resourceLocator.IndexOf('?');
        var pathOnly = queryIndex > -1
            ? resourceLocator[..queryIndex]
            : resourceLocator;

        pathOnly = pathOnly
            .Replace("//", "/");

        var fullPath = hostingDirectory + (pathOnly.EndsWith("/")
            ? $"{pathOnly}index.html"
            : pathOnly);

        var targetResource = new FileInfo(fullPath);
        EnsureAllowedResource(targetResource);

        if (targetResource.Exists)
        {
            httpResponse.Append(Constants.HttpResponseOk);
            httpResponse.Append(Constants.HttpContentSeparator);

            var fileContent = await File.ReadAllTextAsync(targetResource.FullName, Encoding.UTF8);
            httpResponse.Append(fileContent);
        }
        else
        {
            httpResponse.Append(Constants.HttpResponseNotFound);
        }
    }
    catch (SecurityException)
    {
        httpResponse.Append(Constants.HttpResponseForbidden);
    }

    var httpResponseStr = httpResponse.ToString();
    Console.WriteLine($"Server -> Client {socket.RemoteEndPoint}: {httpResponseStr}");
    var encodedContent = Encoding.UTF8.GetBytes(httpResponseStr);
    _ = await socket.SendAsync(encodedContent);
}

void EnsureAllowedResource(FileInfo targetResource)
{
    if (allowedResourceDirectories.Any(allowedDirectory => allowedDirectory.FullName.Equals(targetResource.DirectoryName!)))
    {
        return;
    }

    throw new SecurityException("Unauthorized resource!");
}

HashSet<DirectoryInfo> ScanAllowedResourceDirectories(IEnumerable<string> permittedResourceDirectories)
{
    var hs = new HashSet<DirectoryInfo>();
    foreach (var dir in permittedResourceDirectories)
    {
        hs.Add(new DirectoryInfo(dir));

        var subDirs = Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories);
        foreach (var subDir in subDirs)
        {
            hs.Add(new DirectoryInfo(subDir));
        }
    }
    return hs;
}

internal static class Constants
{
    public const string HttpMethodGet = "GET";

    public const string HttpResponseOk = "200 OK";
    public const string HttpResponseNotFound = "404 Not Found";
    public const string HttpResponseForbidden = "403 Forbidden";

    public const string HttpHeaderSeparator = "\r\n";
    public const string HttpContentSeparator = "\r\n\r\n";
}

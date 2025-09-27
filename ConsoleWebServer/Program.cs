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

var rootCommand = new Command(name: "run", description: "Primitive Console Web Server");

rootCommand.AddOption(portOption);
rootCommand.AddOption(permittedDirectoriesOption);

rootCommand.SetHandler(async (userPort, userDirs) =>
{
    port = userPort;
    permittedResourceDirectories = userDirs;

    Console.WriteLine("Warming up...");

    var allowedResourceDirectories = ScanAllowedResourceDirectories(permittedResourceDirectories);
    var clients = new Dictionary<EndPoint, Socket>();

    try
    {
        var listener = new TcpListener(localaddr: IPAddress.Any, port: port);
        listener.Start();

        Console.WriteLine($"Ready to accept clients on port: {port}");

        await ListenerLoop(listener, allowedResourceDirectories, clients);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to start! {ex}");
    }
},
portOption,
permittedDirectoriesOption);

await rootCommand.InvokeAsync(args);

async Task ListenerLoop(TcpListener listener, HashSet<DirectoryInfo> allowedResourceDirectories, Dictionary<EndPoint, Socket> clients)
{
    while (true)
    {
        var socket = await listener.AcceptSocketAsync();
        var remoteEndpoint = socket.RemoteEndPoint;

        if (remoteEndpoint is not null)
        {
            clients.Add(socket.RemoteEndPoint!, socket);
            _ = HandleNewClient(socket, allowedResourceDirectories, clients);
        }
        else
        {
            Console.WriteLine("Failed to accept socket. No remote endpoint.");
        }
    }
}

async Task HandleNewClient(Socket socket, HashSet<DirectoryInfo> allowedResourceDirectories, Dictionary<EndPoint, Socket> clients)
{
    try
    {
        Console.WriteLine($"New client connected: {socket.RemoteEndPoint}");

        var message = await ReadData(socket, Encoding.UTF8);
        await TryHandleHttpRequest(message, socket, allowedResourceDirectories);

        Console.WriteLine($"Client {socket.RemoteEndPoint} -> Server: {message}");
    }
    catch (Exception ex)
    {
        try
        {
            Console.WriteLine($"Failed to handle new client ({socket.RemoteEndPoint})! {ex}");
            socket.Close();
        }
        catch (Exception ex2)
        {
            var aggregateEx = new AggregateException(ex, ex2);
            Console.WriteLine($"Failed to handle new client and socket is unreadable! {aggregateEx}");
        }
    }
}

async Task<string> ReadData(Socket socket, Encoding encoding)
{
    var buffer = new byte[1024];
    _ = await socket.ReceiveAsync(buffer);
    var message = encoding.GetString(buffer);
    return message.Trim();
}

async Task TryHandleHttpRequest(string message, Socket socket, HashSet<DirectoryInfo> allowedResourceDirectories)
{
    if (message.Length == 0)
    {
        socket.Close();
        return;
    }

    var lines = message.Split("\r\n");

    if (lines.Length <= 0)
    {
        socket.Close();
        return;
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
        return;
    }

    var method = splitHttpRequestLine[0];
    var resourceLocator = splitHttpRequestLine[1];
    var httpVersion = splitHttpRequestLine[2];

    switch (method.ToUpperInvariant())
    {
        case Constants.HttpMethodGet:
            await HandleHttpGetRequest(resourceLocator, httpVersion, splitHttpRequestLine, socket, allowedResourceDirectories);
            break;
        default:
            break;
    }
}

async Task HandleHttpGetRequest(string resourceLocator, string httpVersion, string[] splitHttpRequestLine, Socket socket, HashSet<DirectoryInfo> allowedResourceDirectories)
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

        // Try to find the resource in the allowed directories
        DirectoryInfo? matchingDirectory = null;
        FileInfo? matchingFile = null;
        
        foreach (var allowedDir in allowedResourceDirectories)
        {
            var fullPath = Path.Combine(allowedDir.FullName, pathOnly.TrimStart('/'));
            
            if (pathOnly.EndsWith("/"))
            {
                var directory = new DirectoryInfo(fullPath);
                if (directory.Exists)
                {
                    matchingDirectory = directory;
                    break;
                }
            }
            else
            {
                var file = new FileInfo(fullPath);
                if (file.Exists)
                {
                    matchingFile = file;
                    break;
                }
            }
        }

        if (matchingDirectory != null && pathOnly.EndsWith("/"))
        {
            // Generate directory listing
            var directoryListing = GenerateDirectoryListing(matchingDirectory, pathOnly, allowedResourceDirectories);
            
            httpResponse.AppendLine(Constants.HttpResponseOk);
            httpResponse.AppendLine("Content-Type: text/html");
            httpResponse.AppendLine($"Content-Length: {directoryListing.Length}");
            httpResponse.AppendLine();
            httpResponse.Append(directoryListing);
        }
        else if (matchingFile != null)
        {
            // Serve the file
            httpResponse.AppendLine(Constants.HttpResponseOk);

            var fileContent = await File.ReadAllTextAsync(matchingFile.FullName, Encoding.UTF8);
            httpResponse.AppendLine($"Content-Length: {fileContent.Length}");
            httpResponse.AppendLine();
            httpResponse.Append(fileContent);
        }
        else
        {
            // Try to find index.html in directories if path ends with /
            if (pathOnly.EndsWith("/"))
            {
                foreach (var allowedDir in allowedResourceDirectories)
                {
                    var indexPath = Path.Combine(allowedDir.FullName, pathOnly.TrimStart('/'), "index.html");
                    var indexFile = new FileInfo(indexPath);
                    if (indexFile.Exists)
                    {
                        httpResponse.AppendLine(Constants.HttpResponseOk);
                        var fileContent = await File.ReadAllTextAsync(indexFile.FullName, Encoding.UTF8);
                        httpResponse.AppendLine($"Content-Length: {fileContent.Length}");
                        httpResponse.AppendLine();
                        httpResponse.Append(fileContent);
                        break;
                    }
                }
            }
            
            if (!httpResponse.ToString().Contains(Constants.HttpResponseOk))
            {
                httpResponse.Append(Constants.HttpResponseNotFound);
            }
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

string GenerateDirectoryListing(DirectoryInfo directory, string requestPath, HashSet<DirectoryInfo> allowedResourceDirectories)
{
    var html = new StringBuilder();
    html.AppendLine("<!DOCTYPE html>");
    html.AppendLine("<html>");
    html.AppendLine("<head>");
    html.AppendLine($"<title>Index of {requestPath}</title>");
    html.AppendLine("</head>");
    html.AppendLine("<body>");
    html.AppendLine($"<h1>Index of {requestPath}</h1>");
    html.AppendLine("<hr>");
    html.AppendLine("<pre>");
    
    // Add parent directory link if not at root
    if (requestPath != "/" && CanNavigateToParent(directory, allowedResourceDirectories))
    {
        var parentPath = requestPath.TrimEnd('/');
        var lastSlash = parentPath.LastIndexOf('/');
        var parentRequestPath = lastSlash <= 0 ? "/" : parentPath[..lastSlash] + "/";
        html.AppendLine($"<a href=\"{parentRequestPath}\">../</a>");
    }
    
    try
    {
        // List directories first
        var directories = directory.GetDirectories()
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        
        foreach (var dir in directories)
        {
            var dirPath = requestPath.EndsWith("/") ? requestPath + dir.Name + "/" : requestPath + "/" + dir.Name + "/";
            html.AppendLine($"<a href=\"{dirPath}\">{dir.Name}/</a>");
        }
        
        // Then list files
        var files = directory.GetFiles()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
            
        foreach (var file in files)
        {
            var filePath = requestPath.EndsWith("/") ? requestPath + file.Name : requestPath + "/" + file.Name;
            var formattedSize = FormatFileSize(file.Length);
            var lastModified = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            html.AppendLine($"<a href=\"{filePath}\">{file.Name}</a>    {lastModified}    {formattedSize}");
        }
    }
    catch (UnauthorizedAccessException)
    {
        html.AppendLine("Access denied to this directory.");
    }
    
    html.AppendLine("</pre>");
    html.AppendLine("<hr>");
    html.AppendLine("</body>");
    html.AppendLine("</html>");
    
    return html.ToString();
}

bool CanNavigateToParent(DirectoryInfo directory, HashSet<DirectoryInfo> allowedResourceDirectories)
{
    var parent = directory.Parent;
    if (parent == null) return false;
    
    // Check if parent is within allowed directories
    return allowedResourceDirectories.Any(allowedDirectory => 
        parent.FullName.Equals(allowedDirectory.FullName) || 
        parent.FullName.StartsWith(allowedDirectory.FullName + Path.DirectorySeparatorChar));
}

string FormatFileSize(long bytes)
{
    string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
    int counter = 0;
    decimal number = bytes;
    while (Math.Round(number / 1024) >= 1)
    {
        number /= 1024;
        counter++;
    }
    return $"{number:n1}{suffixes[counter]}";
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
}

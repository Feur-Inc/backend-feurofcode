using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Net;
using System.Net.WebSockets;
using System.Threading;


public class RequestData
{
    public string Lang { get; set; }
    public string Code { get; set; }
    public string ID { get; set; }
    public List<Attachment> Attachments { get; set; }
}

public class Attachment
{
    public string Name { get; set; }
    public string Content { get; set; }
}

public class Startup
{
    private const string PYTHON_LANG = "python";
    private const string BASH_LANG = "bash";
    private const string NB_PREFIX = "nb-";
    private const string ROOT_PATH = "/root";
    private readonly DockerClient _dockerClient;

    public Startup()
    {
        _dockerClient = new DockerClientConfiguration(
            new Uri("unix:///var/run/docker.sock"))
                .CreateClient();
    }

    private Dictionary<string, (MultiplexedStream Stream, CancellationTokenSource CancellationSource, string FolderPath)> _containerData = new Dictionary<string, (MultiplexedStream, CancellationTokenSource, string)>();

    public void Configure(IApplicationBuilder app)
    {
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/ws")
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await HandleWebSocketConnection(webSocket);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            }
            else
            {
                await next();
            }
        });

        app.Run(HandleRequest);
    }

    private async Task HandleRequest(HttpContext context)
    {
        string responseString = "";

        try
        {
            if (context.Request.Method == "GET")
            {
                switch (context.Request.Path)
                {
                    case "/nb_create":
                        responseString = await HandleNbCreate(context);
                        break;
                    case "/nb_list":
                        responseString = await HandleNbList(context);
                        break;
                    default:
                        responseString = JsonConvert.SerializeObject(new { error = "Invalid GET request path." });
                        break;
                }
            }
            else if (context.Request.Method == "POST")
            {
                if (context.Request.ContentType != "application/json")
                {
                    responseString = JsonConvert.SerializeObject(new { error = "Invalid content type. Only application/json is supported." });
                }
                else
                {
                    switch (context.Request.Path)
                    {
                        case "/nb_run":
                            responseString = await HandleNbRun(context);
                            break;
                        case "/nb_delete":
                            responseString = await HandleNbDelete(context);
                            break;
                        case "/nb_pause":
                            responseString = await HandleNbPause(context);
                            break;
                        case "/nb_resume":
                            responseString = await HandleNbResume(context);
                            break;
                        case "/run":
                            responseString = await HandleRun(context);
                            break;
                        case "/run_interactive":
                            responseString = await HandleRunInteractive(context);
                            break;
                        default:
                            responseString = JsonConvert.SerializeObject(new { error = "Invalid POST request path." });
                            break;
                    }
                }
            }
            else
            {
                responseString = JsonConvert.SerializeObject(new { error = "Invalid request method. Only GET and POST are supported." });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
            responseString = JsonConvert.SerializeObject(new { error = "An internal server error occurred." });
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(responseString);
    }

    private async Task<string> HandleRunInteractive(HttpContext context)
    {
        var data = await DeserializeRequestBody<RequestData>(context);
        if (data == null) return JsonConvert.SerializeObject(new { error = "Invalid JSON format." });

        if (data.Lang != PYTHON_LANG)
        {
            return JsonConvert.SerializeObject(new { error = "Only Python is supported for interactive mode." });
        }

        string randomString = GenerateRandomString(10);
        string langPath = Path.Combine(ROOT_PATH, PYTHON_LANG, randomString);
        string appPath = Path.Combine(langPath, "app");
        string attachmentsPath = Path.Combine(langPath, "attachments");

        Directory.CreateDirectory(appPath);
        Directory.CreateDirectory(attachmentsPath);

        if (data.Attachments != null)
        {
            foreach (var attachment in data.Attachments)
            {
                var fileContent = Convert.FromBase64String(attachment.Content);
                File.WriteAllBytes(Path.Combine(attachmentsPath, attachment.Name), fileContent);
            }
        }

        Process.Start("chmod", $"-R 777 {attachmentsPath}");

        string filePath = Path.Combine(appPath, "launch.py");
        File.WriteAllText(filePath, data.Code.Replace("\n", Environment.NewLine));

        var createContainerResponse = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{PYTHON_LANG}-docker",
            Name = randomString,
            HostConfig = new HostConfig
            {
                Binds = new List<string> 
                { 
                    $"{appPath}:/app",
                    $"{attachmentsPath}:/home/appuser"
                }
            },
            Tty = true,
            OpenStdin = true,
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Cmd = new[] { "bash", "-c", "sleep 0.1 && python3 /app/launch.py" }
        });

        var cts = new CancellationTokenSource();
        _containerData[randomString] = (null, cts, langPath);

        return JsonConvert.SerializeObject(new { id = randomString });
    }

    private async Task HandleWebSocketConnection(WebSocket webSocket)
    {
        var buffer = new byte[1024 * 4];
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var idMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var idObject = JsonConvert.DeserializeObject<Dictionary<string, string>>(idMessage);

        if (!idObject.ContainsKey("id") || !_containerData.ContainsKey(idObject["id"]))
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid container ID", CancellationToken.None);
            return;
        }

        var containerId = idObject["id"];
        var (_, cts, _) = _containerData[containerId];

        await _dockerClient.Containers.StartContainerAsync(containerId, null);

        var containerStream = await _dockerClient.Containers.AttachContainerAsync(containerId, true, new ContainerAttachParameters
        {
            Stream = true,
            Stdin = true,
            Stdout = true,
            Stderr = true
        });

        _containerData[containerId] = (containerStream, cts, _containerData[containerId].Item3);

        _ = Task.Delay(TimeSpan.FromMinutes(1), cts.Token).ContinueWith(t => 
        {
            if (!t.IsCanceled)
            {
                _dockerClient.Containers.KillContainerAsync(containerId, new ContainerKillParameters()).Wait();
                CleanupContainer(containerId);
            }
        });

        var outputBuffer = new byte[1024];

        var readTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var readResult = await containerStream.ReadOutputAsync(outputBuffer, 0, outputBuffer.Length, cts.Token);
                    if (readResult.Count > 0)
                    {
                        await webSocket.SendAsync(new ArraySegment<byte>(outputBuffer, 0, readResult.Count), WebSocketMessageType.Text, true, cts.Token);
                    }
                    else
                    {
                        await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("Python program has finished execution.")), WebSocketMessageType.Text, true, CancellationToken.None);
                        CleanupContainer(containerId);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("Execution time limit reached. Terminating.")), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Execution finished", CancellationToken.None);
                CleanupContainer(containerId);
            }
        });

        while (webSocket.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
        {
            result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string command = Encoding.UTF8.GetString(buffer, 0, result.Count);
                byte[] commandBytes = Encoding.UTF8.GetBytes(command + "\n");
                await containerStream.WriteAsync(commandBytes, 0, commandBytes.Length, cts.Token);
            }
        }

        cts.Cancel();
        await readTask;
    }

    private void CleanupContainer(string containerId)
    {
        if (_containerData.TryGetValue(containerId, out var containerInfo))
        {
            var (stream, cts, folderPath) = containerInfo;
            cts.Cancel();
            stream?.Dispose();
            _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }).Wait();
            Directory.Delete(folderPath, true);
            _containerData.Remove(containerId);
        }
    }

    private async Task<string> HandleNbCreate(HttpContext context)
    {
        try
        {
            string randomString = GenerateRandomString(10);
            string nbPath = Path.Combine(ROOT_PATH, "nb", randomString);
    
            Directory.CreateDirectory(nbPath);
            Directory.CreateDirectory(Path.Combine(nbPath, "api"));
            Directory.CreateDirectory(Path.Combine(nbPath, "attachments"));
    
            string apiPath = Path.Combine(nbPath, "api");
            string attachmentsPath = Path.Combine(nbPath, "attachments");
            
            File.WriteAllText(Path.Combine(apiPath, "exe"), "");
            File.WriteAllText(Path.Combine(apiPath, "out"), "");
    
            Process.Start("chmod", $"-R 777 {nbPath}");
    
            var createContainerResponse = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = "python-nb",
                Name = randomString,
                HostConfig = new HostConfig
                {
                    Binds = new List<string>
                    { 
                        $"{apiPath}:/api",
                        $"{attachmentsPath}:/home/appuser"
                    }
                }
            });
    
            await _dockerClient.Containers.StartContainerAsync(createContainerResponse.ID, null);
    
            return JsonConvert.SerializeObject(new { id = $"{NB_PREFIX}{randomString}"});
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erreur: {ex.Message}");
            return JsonConvert.SerializeObject(new { error = "An error occured while creating the notebook" });
        }
    }

    private async Task<string> HandleNbRun(HttpContext context)
    {
        var data = await DeserializeRequestBody<RequestData>(context);
        if (data == null) return JsonConvert.SerializeObject(new { error = "Invalid JSON format." });

        if (string.IsNullOrEmpty(data.ID) || !data.ID.StartsWith(NB_PREFIX))
        {
            return JsonConvert.SerializeObject(new { error = "Invalid notebook ID" });
        }

        string id = data.ID.Replace(NB_PREFIX, "");
        string nbPath = Path.Combine(ROOT_PATH, "nb", id);

        if (!Directory.Exists(nbPath))
        {
            return JsonConvert.SerializeObject(new { error = "Notebook not found" });
        }

        var containerInfo = await _dockerClient.Containers.InspectContainerAsync(id);
        if (containerInfo.State.Status != "running")
        {
            return JsonConvert.SerializeObject(new { error = "This notebook is paused" });
        }

        if (data.Lang != PYTHON_LANG)
        {
            return JsonConvert.SerializeObject(new { error = "Invalid language for notebook" });
        }

        var stopwatch = Stopwatch.StartNew();

        string apiPath = Path.Combine(nbPath, "api");
        string attachmentsPath = Path.Combine(nbPath, "attachments");
 
        if (data.Attachments != null)
        {
            foreach (var attachment in data.Attachments)
            {
                var fileContent = Convert.FromBase64String(attachment.Content);
                File.WriteAllBytes(Path.Combine(attachmentsPath, attachment.Name), fileContent);
            }
        }

        string outputPath = Path.Combine(apiPath, "out");
        string exePath = Path.Combine(apiPath, "exe");

        FileInfo fileInfo = new FileInfo(outputPath);
        DateTime lastWriteTime = fileInfo.LastWriteTime;
        
        File.WriteAllText(exePath, data.Code);
        
        while (fileInfo.LastWriteTime <= lastWriteTime)
        {
            await Task.Delay(100);
            fileInfo.Refresh();
        }

        string[] files = Directory.GetFiles(attachmentsPath);
        if (files.Length > 0)
        {
            List<Attachment> attachments = new List<Attachment>();
            foreach (string file in files)
            {
                attachments.Add(new Attachment
                {
                    Name = Path.GetFileName(file),
                    Content = Convert.ToBase64String(File.ReadAllBytes(file))
                });
            }
            stopwatch.Stop();

            return JsonConvert.SerializeObject(new { result = File.ReadAllText(outputPath), attachments_out = attachments, executionTime = stopwatch.Elapsed.TotalSeconds });
        }

        stopwatch.Stop();
        string output2 = File.ReadAllText(outputPath);
        return JsonConvert.SerializeObject(new { result = output2, executionTime = stopwatch.Elapsed.TotalSeconds });
    }

    private async Task<string> HandleNbDelete(HttpContext context)
    {
        var data = await DeserializeRequestBody<RequestData>(context);
        
        if (data == null) return JsonConvert.SerializeObject(new { error = "Invalid JSON format." });

        string id = data.ID;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(NB_PREFIX))
        {
            return JsonConvert.SerializeObject(new { error = "Invalid notebook ID" });
        }

        string nbPath = Path.Combine(ROOT_PATH, "nb", id.Replace(NB_PREFIX, ""));
        if (Directory.Exists(nbPath))
        {
            Directory.Delete(nbPath, true);
        }

        await _dockerClient.Containers.KillContainerAsync(id.Replace(NB_PREFIX, ""), new ContainerKillParameters());

        await _dockerClient.Containers.RemoveContainerAsync(id.Replace(NB_PREFIX, ""), new ContainerRemoveParameters());

        return JsonConvert.SerializeObject(new { result = "Notebook deleted" });
    }


    private async Task<string> HandleNbPause(HttpContext context)
    {
        var data = await DeserializeRequestBody<RequestData>(context);
        
        if (data == null) return JsonConvert.SerializeObject(new { error = "Invalid JSON format." });

        string id = data.ID;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(NB_PREFIX))
        {
            return JsonConvert.SerializeObject(new { error = "Invalid notebook ID" });
        }

        await _dockerClient.Containers.PauseContainerAsync(id.Replace(NB_PREFIX, ""));

        return JsonConvert.SerializeObject(new { result = "Notebook paused" });
    }

    private async Task<string> HandleNbResume(HttpContext context)
    {
        var data = await DeserializeRequestBody<RequestData>(context);
        
        if (data == null) return JsonConvert.SerializeObject(new { error = "Invalid JSON format." });

        string id = data.ID;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(NB_PREFIX))
        {
            return JsonConvert.SerializeObject(new { error = "Invalid notebook ID" });
        }

        await _dockerClient.Containers.UnpauseContainerAsync(id.Replace(NB_PREFIX, ""));

        return JsonConvert.SerializeObject(new { result = "Notebook resumed" });
    }

    private async Task<string> HandleNbList(HttpContext context)
    {
        var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
        
        var notebooks = containers
            .Where(c => c.Names.Any(n => n.StartsWith("/")))
            .Select(c => new Dictionary<string, string> 
            {
                { "id", $"{NB_PREFIX}{c.Names[0].TrimStart('/')}" },
                { "state", c.State }
            })
            .ToList();

        foreach (var notebook in notebooks)
        {
            if (notebook["state"] == "exited")
            {
                notebook["state"] = "paused";
            }
        }

        return JsonConvert.SerializeObject(new { notebooks });
    }

    private async Task<string> HandleRun(HttpContext context)
    {
        var data = await DeserializeRequestBody<RequestData>(context);
        if (data == null) return JsonConvert.SerializeObject(new { error = "Invalid JSON format." });

        switch (data.Lang)
        {
            case PYTHON_LANG:
                return await Runner(PYTHON_LANG, data.Code, data.Attachments);
            case BASH_LANG:
                return await Runner(BASH_LANG, data.Code, data.Attachments);
            default:
                return JsonConvert.SerializeObject(new { error = "Invalid language" });
        }
    }

    private async Task<T> DeserializeRequestBody<T>(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        try
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
        catch (JsonException e)
        {
            Console.WriteLine(e.Message);
            return default;
        }
    }

    private async Task<string> Runner(string lang, string code, List<Attachment> attachments)
    {
        string randomString = GenerateRandomString(10);
        string langPath = Path.Combine(ROOT_PATH, lang, randomString);
        string appPath = Path.Combine(langPath, "app");
        string attachmentsPath = Path.Combine(langPath, "attachments");

        Directory.CreateDirectory(appPath);
        Directory.CreateDirectory(attachmentsPath);

        if (attachments != null)
        {
            foreach (var attachment in attachments)
            {
                var fileContent = Convert.FromBase64String(attachment.Content);
                File.WriteAllBytes(Path.Combine(attachmentsPath, attachment.Name), fileContent);
            }
        }

        Process.Start("chmod", $"-R 777 {attachmentsPath}");

        string fileExtension = lang == PYTHON_LANG ? "py" : "sh";
        string filePath = Path.Combine(appPath, $"launch.{fileExtension}");
        File.WriteAllText(filePath, code.Replace("\n", Environment.NewLine));

        var createContainerResponse = await _dockerClient.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{lang}-docker",
            Name = randomString,
            HostConfig = new HostConfig
            {
                Binds = new List<string> 
                { 
                    $"{appPath}:/app",
                    $"{attachmentsPath}:/home/appuser"
                }
            },
            Tty = true
        });

        var stopwatch = Stopwatch.StartNew();
        await _dockerClient.Containers.StartContainerAsync(createContainerResponse.ID, null);

        var waitTask = _dockerClient.Containers.WaitContainerAsync(createContainerResponse.ID);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10.20));

        if (await Task.WhenAny(waitTask, timeoutTask) == timeoutTask)
        {
            await _dockerClient.Containers.StopContainerAsync(createContainerResponse.ID, new ContainerStopParameters());
        }

        stopwatch.Stop();

        var logs = await _dockerClient.Containers.GetContainerLogsAsync(createContainerResponse.ID, new ContainerLogsParameters { ShowStdout = true, ShowStderr = true });
        
        using var reader = new StreamReader(logs);
        var result = await reader.ReadToEndAsync();

        result = string.IsNullOrWhiteSpace(result) ? "CES: Empty output" : result.Replace("KeyboardInterrupt", "Execution timed out.");

        if (attachments != null)
        {
            foreach (var attachment in attachments)
            {
                File.Delete(Path.Combine(appPath, attachment.Name));
            }
        }

        var filesList = Directory.GetFiles(attachmentsPath)
            .Take(10)
            .Select(file => new Attachment
            {
                Name = Path.GetFileName(file),
                Content = Convert.ToBase64String(File.ReadAllBytes(file))
            })
            .ToList();

        Directory.Delete(langPath, true);

        await _dockerClient.Containers.RemoveContainerAsync(createContainerResponse.ID, new ContainerRemoveParameters());

        return JsonConvert.SerializeObject(new
        {
            result = result,
            executionTime = stopwatch.Elapsed.TotalSeconds,
            attachments_out = filesList
        });
    }

    private string GenerateRandomString(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz";
        var random = new Random();
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

}

public class Program
{
    public static void Main(string[] args)
    {
        var host = new WebHostBuilder()
            .UseKestrel()
            .UseStartup<Startup>()
            .UseUrls("http://0.0.0.0:8080")
            .Build();
        host.Run();
    }
}

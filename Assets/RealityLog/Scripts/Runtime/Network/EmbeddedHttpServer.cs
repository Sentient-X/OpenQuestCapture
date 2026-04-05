# nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Network
{
    /// <summary>
    /// Lightweight TCP-based HTTP/1.1 server for Unity Android.
    /// Runs on a background thread; dispatches actions to the Unity main thread via a queue.
    /// </summary>
    public class EmbeddedHttpServer
    {
        public int Port { get; }
        private TcpListener? listener;
        private Thread? listenerThread;
        private volatile bool running;

        private readonly ConcurrentQueue<Action> mainThreadQueue = new();
        private readonly Dictionary<string, Func<HttpRequest, HttpResponse>> getRoutes = new();
        private readonly Dictionary<string, Func<HttpRequest, HttpResponse>> postRoutes = new();
        private readonly Dictionary<string, Func<HttpRequest, HttpResponse>> deleteRoutes = new();

        // For wildcard routes like /api/recordings/:filename
        private readonly List<(string prefix, string method, Func<HttpRequest, HttpResponse> handler)> wildcardRoutes = new();

        public EmbeddedHttpServer(int port = 8080)
        {
            Port = port;
        }

        // ── Route registration ──

        public void Get(string path, Func<HttpRequest, HttpResponse> handler) => getRoutes[path] = handler;
        public void Post(string path, Func<HttpRequest, HttpResponse> handler) => postRoutes[path] = handler;
        public void Delete(string path, Func<HttpRequest, HttpResponse> handler) => deleteRoutes[path] = handler;

        /// <summary>
        /// Register a wildcard route. The prefix is matched and the remainder is passed as pathParam.
        /// e.g., WildcardGet("/api/recordings/", handler) matches /api/recordings/foo.mp4
        /// </summary>
        public void WildcardGet(string prefix, Func<HttpRequest, HttpResponse> handler)
            => wildcardRoutes.Add((prefix, "GET", handler));

        public void WildcardDelete(string prefix, Func<HttpRequest, HttpResponse> handler)
            => wildcardRoutes.Add((prefix, "DELETE", handler));

        // ── Lifecycle ──

        public void Start()
        {
            if (running) return;
            running = true;

            listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "HttpServer"
            };
            listenerThread.Start();
            Debug.Log($"[{Constants.LOG_TAG}] EmbeddedHttpServer: Starting on port {Port}");
        }

        public void Stop()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            listenerThread = null;
            Debug.Log($"[{Constants.LOG_TAG}] EmbeddedHttpServer: Stopped");
        }

        /// <summary>
        /// Must be called from MonoBehaviour.Update() to process main-thread callbacks.
        /// </summary>
        public void ProcessMainThreadQueue()
        {
            while (mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] EmbeddedHttpServer main-thread action error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Enqueue an action to run on the Unity main thread. Returns a ManualResetEventSlim
        /// that the caller can wait on to get the result.
        /// </summary>
        public ManualResetEventSlim EnqueueOnMainThread(Action action)
        {
            var signal = new ManualResetEventSlim(false);
            mainThreadQueue.Enqueue(() =>
            {
                try { action(); }
                finally { signal.Set(); }
            });
            return signal;
        }

        // ── TCP listener loop ──

        private void ListenLoop()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                Debug.Log($"[{Constants.LOG_TAG}] EmbeddedHttpServer: Listening on 0.0.0.0:{Port}");

                // Log all network interfaces so we know what IPs the Quest has
                try
                {
                    var hostName = Dns.GetHostName();
                    Debug.Log($"[{Constants.LOG_TAG}] EmbeddedHttpServer: Hostname: {hostName}");
                    var addresses = Dns.GetHostAddresses(hostName);
                    foreach (var addr in addresses)
                    {
                        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            Debug.Log($"[{Constants.LOG_TAG}] EmbeddedHttpServer: IPv4 interface: {addr}:{Port}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] EmbeddedHttpServer: Could not enumerate interfaces: {ex.Message}");
                }

                int connectionCount = 0;

                while (running)
                {
                    try
                    {
                        var client = listener.AcceptTcpClient();
                        connectionCount++;
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 10000;

                        // Log every incoming connection with client IP
                        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                        Debug.Log($"[{Constants.LOG_TAG}] EmbeddedHttpServer: Connection #{connectionCount} from {remoteEndpoint}");

                        ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
                    }
                    catch (SocketException) when (!running)
                    {
                        break; // Expected on shutdown
                    }
                    catch (Exception ex)
                    {
                        if (running)
                            Debug.LogWarning($"[{Constants.LOG_TAG}] EmbeddedHttpServer accept error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] EmbeddedHttpServer listener failed: {ex.Message}");
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var request = ParseRequest(stream);
                    if (request == null)
                    {
                        WriteResponse(stream, HttpResponse.BadRequest("Malformed request"));
                        return;
                    }

                    var response = RouteRequest(request);
                    if (response.FileStreamPath != null)
                    {
                        WriteFileResponse(stream, response, request);
                    }
                    else
                    {
                        WriteResponse(stream, response);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] EmbeddedHttpServer client error: {ex.Message}");
            }
        }

        // ── Request parsing ──

        private HttpRequest? ParseRequest(NetworkStream stream)
        {
            try
            {
                var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                var requestLine = reader.ReadLine();
                if (string.IsNullOrEmpty(requestLine)) return null;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return null;

                var method = parts[0].ToUpperInvariant();
                var rawPath = parts[1];

                // Parse path and query string
                var pathAndQuery = rawPath.Split('?', 2);
                var path = Uri.UnescapeDataString(pathAndQuery[0]);
                var queryString = pathAndQuery.Length > 1 ? pathAndQuery[1] : "";
                var queryParams = ParseQueryString(queryString);

                // Parse headers
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? headerLine;
                while (!string.IsNullOrEmpty(headerLine = reader.ReadLine()))
                {
                    var colonIdx = headerLine.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var key = headerLine.Substring(0, colonIdx).Trim();
                        var val = headerLine.Substring(colonIdx + 1).Trim();
                        headers[key] = val;
                    }
                }

                // Parse body if Content-Length present
                string body = "";
                if (headers.TryGetValue("Content-Length", out var clStr) && int.TryParse(clStr, out var contentLength) && contentLength > 0)
                {
                    var buffer = new char[contentLength];
                    int totalRead = 0;
                    while (totalRead < contentLength)
                    {
                        int read = reader.Read(buffer, totalRead, contentLength - totalRead);
                        if (read == 0) break;
                        totalRead += read;
                    }
                    body = new string(buffer, 0, totalRead);
                }

                return new HttpRequest
                {
                    Method = method,
                    Path = path,
                    QueryParams = queryParams,
                    Headers = headers,
                    Body = body
                };
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] EmbeddedHttpServer parse error: {ex.Message}");
                return null;
            }
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;

            foreach (var pair in query.Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2)
                    result[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                else if (kv.Length == 1)
                    result[Uri.UnescapeDataString(kv[0])] = "";
            }
            return result;
        }

        // ── Routing ──

        private HttpResponse RouteRequest(HttpRequest request)
        {
            try
            {
                Debug.Log($"[{Constants.LOG_TAG}] HTTP: {request.Method} {request.Path}");

                // Try exact match first
                Dictionary<string, Func<HttpRequest, HttpResponse>>? routes = request.Method switch
                {
                    "GET" => getRoutes,
                    "POST" => postRoutes,
                    "DELETE" => deleteRoutes,
                    _ => null
                };

                if (routes != null && routes.TryGetValue(request.Path, out var handler))
                {
                    return handler(request);
                }

                // Try wildcard routes
                foreach (var (prefix, method, wHandler) in wildcardRoutes)
                {
                    if (request.Method == method && request.Path.StartsWith(prefix) && request.Path.Length > prefix.Length)
                    {
                        request.PathParam = request.Path.Substring(prefix.Length);
                        return wHandler(request);
                    }
                }

                return HttpResponse.NotFound($"No route for {request.Method} {request.Path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] EmbeddedHttpServer route error: {ex}");
                return HttpResponse.InternalError(ex.Message);
            }
        }

        // ── Response writing ──

        private static void WriteResponse(NetworkStream stream, HttpResponse response)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(response.Body ?? "");
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {response.StatusCode} {response.StatusText}\r\n");
            sb.Append($"Content-Type: {response.ContentType}\r\n");
            sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bodyBytes.Length > 0)
                stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static void WriteFileResponse(NetworkStream stream, HttpResponse response, HttpRequest request)
        {
            var filePath = response.FileStreamPath!;
            if (!File.Exists(filePath))
            {
                WriteResponse(stream, HttpResponse.NotFound("File not found"));
                return;
            }

            var fileInfo = new FileInfo(filePath);
            long fileSize = fileInfo.Length;
            long rangeStart = 0;
            long rangeEnd = fileSize - 1;
            bool isPartial = false;

            // Parse Range header
            if (request.Headers.TryGetValue("Range", out var rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var rangeParts = rangeHeader.Substring(6).Split('-');
                if (rangeParts.Length == 2)
                {
                    if (long.TryParse(rangeParts[0], out var rs)) rangeStart = rs;
                    if (!string.IsNullOrEmpty(rangeParts[1]) && long.TryParse(rangeParts[1], out var re)) rangeEnd = re;
                    isPartial = true;
                }
            }

            long contentLength = rangeEnd - rangeStart + 1;
            var sb = new StringBuilder();

            if (isPartial)
            {
                sb.Append($"HTTP/1.1 206 Partial Content\r\n");
                sb.Append($"Content-Range: bytes {rangeStart}-{rangeEnd}/{fileSize}\r\n");
            }
            else
            {
                sb.Append($"HTTP/1.1 200 OK\r\n");
            }

            sb.Append($"Content-Type: {response.ContentType}\r\n");
            sb.Append($"Content-Length: {contentLength}\r\n");
            sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);

            // Stream file content
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(rangeStart, SeekOrigin.Begin);
                var buffer = new byte[65536];
                long remaining = contentLength;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = fs.Read(buffer, 0, toRead);
                    if (read == 0) break;
                    stream.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            stream.Flush();
        }
    }

    // ── Request / Response models ──

    public class HttpRequest
    {
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public string PathParam { get; set; } = "";
        public Dictionary<string, string> QueryParams { get; set; } = new();
        public Dictionary<string, string> Headers { get; set; } = new();
        public string Body { get; set; } = "";
    }

    public class HttpResponse
    {
        public int StatusCode { get; set; } = 200;
        public string StatusText { get; set; } = "OK";
        public string ContentType { get; set; } = "application/json";
        public string? Body { get; set; }
        public string? FileStreamPath { get; set; }

        public static HttpResponse Json(int statusCode, string json) => new()
        {
            StatusCode = statusCode,
            StatusText = StatusTextFor(statusCode),
            Body = json
        };

        public static HttpResponse Ok(string json) => Json(200, json);

        public static HttpResponse NotFound(string message) => Json(404,
            $"{{\"error\":\"not_found\",\"message\":\"{EscapeJson(message)}\"}}");

        public static HttpResponse Conflict(string error, string message) => Json(409,
            $"{{\"error\":\"{EscapeJson(error)}\",\"message\":\"{EscapeJson(message)}\"}}");

        public static HttpResponse BadRequest(string message) => Json(400,
            $"{{\"error\":\"bad_request\",\"message\":\"{EscapeJson(message)}\"}}");

        public static HttpResponse InternalError(string message) => Json(500,
            $"{{\"error\":\"internal_error\",\"message\":\"{EscapeJson(message)}\"}}");

        public static HttpResponse StorageFull(long freeBytes, long requiredBytes) => Json(507,
            $"{{\"error\":\"storage_full\",\"message\":\"Insufficient storage to start recording\",\"storageFreeBytes\":{freeBytes},\"storageRequiredBytes\":{requiredBytes}}}");

        public static HttpResponse File(string filePath, string contentType = "video/mp4") => new()
        {
            StatusCode = 200,
            StatusText = "OK",
            ContentType = contentType,
            FileStreamPath = filePath
        };

        private static string StatusTextFor(int code) => code switch
        {
            200 => "OK",
            206 => "Partial Content",
            400 => "Bad Request",
            404 => "Not Found",
            409 => "Conflict",
            500 => "Internal Server Error",
            507 => "Insufficient Storage",
            _ => "Unknown"
        };

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}

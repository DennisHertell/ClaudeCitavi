using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeCitavi.Bridge
{
    /// <summary>
    /// Minimal HTTP server bound to localhost only.
    ///
    ///   POST /rpc   {"method": "references.search", "params": {...}, "project": "optional project name"}
    ///   ->          {"ok": true, "result": ...} | {"ok": false, "error": {"code": "...", "message": "..."}}
    ///
    /// Every request must carry "Authorization: Bearer &lt;token&gt;" (see BridgeConfig).
    /// </summary>
    public sealed class BridgeServer
    {
        private readonly BridgeConfig _config;
        private readonly RpcDispatcher _dispatcher;
        private HttpListener _listener;
        private Thread _thread;

        public BridgeServer(BridgeConfig config, RpcDispatcher dispatcher)
        {
            _config = config;
            _dispatcher = dispatcher;
        }

        public int Port => _config.Port;

        public void Start()
        {
            _listener = new HttpListener();
            // "localhost" prefixes work without admin rights / urlacl.
            _listener.Prefixes.Add($"http://localhost:{_config.Port}/");
            _listener.Prefixes.Add($"http://127.0.0.1:{_config.Port}/");
            _listener.Start();

            _thread = new Thread(Loop) { IsBackground = true, Name = "ClaudeCitavi.Bridge" };
            _thread.Start();
            BridgeLog.Write($"Bridge listening on http://127.0.0.1:{_config.Port}/ (readOnly={_config.ReadOnly})");
        }

        public void Stop()
        {
            try { _listener?.Stop(); } catch { /* ignore */ }
        }

        private void Loop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (HttpListenerException) { return; }
                catch (ObjectDisposedException) { return; }

                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            JObject response;
            int status = 200;
            try
            {
                if (!IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address))
                    throw new BridgeException("forbidden", "Only local connections are allowed.", 403);

                var auth = ctx.Request.Headers["Authorization"] ?? "";
                if (!FixedTimeEquals(auth, "Bearer " + _config.Token))
                    throw new BridgeException("unauthorized", "Missing or invalid token.", 401);

                if (ctx.Request.HttpMethod != "POST" || ctx.Request.Url.AbsolutePath.TrimEnd('/') != "/rpc")
                    throw new BridgeException("not_found", "Use POST /rpc.", 404);

                string body;
                using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();

                var request = JObject.Parse(body);
                var method = (string)request["method"];
                var parameters = request["params"] as JObject ?? new JObject();

                var project = (string)request["project"];
                var result = _dispatcher.Execute(method, parameters, _config.ReadOnly, string.IsNullOrWhiteSpace(project) ? null : project);
                response = new JObject { ["ok"] = true, ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result) };
            }
            catch (BridgeException ex)
            {
                status = ex.HttpStatus;
                response = Error(ex.Code, ex.Message);
            }
            catch (JsonException ex)
            {
                status = 400;
                response = Error("bad_request", "Invalid JSON: " + ex.Message);
            }
            catch (Exception ex)
            {
                var inner = ex.GetBaseException();
                BridgeLog.Write("Unhandled: " + ex);
                status = 500;
                response = Error("internal_error", inner.GetType().Name + ": " + inner.Message);
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(response.ToString(Formatting.None));
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                BridgeLog.Write("Response failed: " + ex.Message);
            }
        }

        private static JObject Error(string code, string message) =>
            new JObject { ["ok"] = false, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        private static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    public sealed class BridgeException : Exception
    {
        public string Code { get; }
        public int HttpStatus { get; }

        public BridgeException(string code, string message, int httpStatus = 400) : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
        }
    }
}

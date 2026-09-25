using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MihaZupan;

namespace ChatSheet.AddIn.Providers
{
    /// <summary>网络代理类型。</summary>
    internal enum ProxyKind
    {
        Direct = 0,
        System = 1,
        Http = 2,
        Https = 3,
        Socks5 = 4,
    }

    /// <summary>
    /// 已解析的代理配置。密码只在发起请求的短生命周期内存在，不能序列化或写日志。
    /// </summary>
    internal sealed class ProxyOptions
    {
        internal string ProfileId { get; set; } = string.Empty;

        internal ProxyKind Kind { get; set; } = ProxyKind.Direct;

        internal string Host { get; set; } = string.Empty;

        internal int Port { get; set; }

        internal string Username { get; set; } = string.Empty;

        internal string Password { get; set; } = string.Empty;
    }

    /// <summary>设置页保存的代理配置。密码不属于明文配置模型。</summary>
    internal sealed class ProxyProfile
    {
        internal string Id { get; set; } = string.Empty;

        internal string Name { get; set; } = string.Empty;

        internal ProxyKind Kind { get; set; } = ProxyKind.Direct;

        internal string Host { get; set; } = string.Empty;

        internal int Port { get; set; }

        internal string Username { get; set; } = string.Empty;
    }

    /// <summary>为 HttpClient 创建代理传输，并提供独立的代理连通性测试。</summary>
    internal static class ProxyTransport
    {
        internal static HttpClientHandler CreateHandler(ProxyOptions options, out HttpToSocks5Proxy socksProxy)
        {
            options = options ?? new ProxyOptions();
            socksProxy = null;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };

            switch (options.Kind)
            {
                case ProxyKind.Direct:
                    handler.UseProxy = false;
                    break;

                case ProxyKind.System:
                    // Proxy 为空时 HttpClientHandler 使用操作系统默认代理。
                    handler.UseProxy = true;
                    break;

                case ProxyKind.Http:
                case ProxyKind.Https:
                    handler.Proxy = CreateHttpProxy(options);
                    handler.UseProxy = true;
                    break;

                case ProxyKind.Socks5:
                    ValidateEndpoint(options);
                    socksProxy = string.IsNullOrWhiteSpace(options.Username)
                        ? new HttpToSocks5Proxy(options.Host, options.Port, 0)
                        : new HttpToSocks5Proxy(
                            options.Host,
                            options.Port,
                            options.Username,
                            options.Password ?? string.Empty,
                            0);
                    handler.Proxy = socksProxy;
                    handler.UseProxy = true;
                    break;

                default:
                    throw new ProviderException("PROXY_INVALID", "不支持的代理类型。");
            }

            return handler;
        }

        internal static async Task<object> TestAsync(
            ProxyOptions options,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            HttpToSocks5Proxy socksProxy = null;
            try
            {
                using (var handler = CreateHandler(options, out socksProxy))
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) })
                using (var response = await client.GetAsync(
                    "https://www.gstatic.com/generate_204",
                    cancellationToken).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return new
                        {
                            ok = false,
                            detail = $"代理已连通，但测试地址返回 {(int)response.StatusCode}。",
                            elapsedMs = stopwatch.ElapsedMilliseconds,
                        };
                    }

                    return new
                    {
                        ok = true,
                        detail = $"代理连接正常（{stopwatch.ElapsedMilliseconds} ms）。",
                        elapsedMs = stopwatch.ElapsedMilliseconds,
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new
                {
                    ok = false,
                    detail = "代理连接失败：" + Describe(ex),
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                };
            }
            finally
            {
                socksProxy?.StopInternalServer();
            }
        }

        internal static void DisposeSocksProxy(HttpToSocks5Proxy socksProxy)
        {
            try { socksProxy?.StopInternalServer(); }
            catch { }
        }

        private static IWebProxy CreateHttpProxy(ProxyOptions options)
        {
            ValidateEndpoint(options);
            var scheme = options.Kind == ProxyKind.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            var uri = new UriBuilder(scheme, options.Host, options.Port).Uri;
            var proxy = new WebProxy(uri);
            if (!string.IsNullOrWhiteSpace(options.Username))
            {
                proxy.Credentials = new NetworkCredential(options.Username, options.Password ?? string.Empty);
            }

            return proxy;
        }

        private static void ValidateEndpoint(ProxyOptions options)
        {
            if (options == null || string.IsNullOrWhiteSpace(options.Host))
            {
                throw new ProviderException("PROXY_HOST_REQUIRED", "请填写代理地址。");
            }

            if (options.Port < 1 || options.Port > 65535)
            {
                throw new ProviderException("PROXY_PORT_INVALID", "代理端口必须是 1-65535。");
            }
        }

        private static string Describe(Exception ex)
        {
            var root = ex;
            while (root.InnerException != null) { root = root.InnerException; }
            return root.Message;
        }
    }
}

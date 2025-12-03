using Emby.Web.GenericEdit.Elements;
using HarmonyLib;
using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnableProxyServer : PatchBase<EnableProxyServer>
    {
        private static MethodInfo _createHttpClientHandler;

        private static readonly string[] BypassAddressList =
        {
            // RFC 1918 Private Address Space
            "10.*",                                                              // 10.0.0.0/8
            "172.16.*", "172.17.*", "172.18.*", "172.19.*",                     // 172.16.0.0/12
            "172.20.*", "172.21.*", "172.22.*", "172.23.*",
            "172.24.*", "172.25.*", "172.26.*", "172.27.*",
            "172.28.*", "172.29.*", "172.30.*", "172.31.*",
            "192.168.*",                                                         // 192.168.0.0/16
            // Loopback
            "127.*", "localhost"                                                 // 127.0.0.0/8
        };

        public EnableProxyServer()
        {
            Initialize();

            if (Plugin.Instance.MainOptionsStore.GetOptions().NetworkOptions.EnableProxyServer)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var applicationHost =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.ApplicationHost");
            _createHttpClientHandler = applicationHost.GetMethod("CreateHttpClientHandler",
                BindingFlags.NonPublic | BindingFlags.Instance);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _createHttpClientHandler,
                postfix: nameof(CreateHttpClientHandlerPostfix));
        }

        [HarmonyPostfix]
        private static void CreateHttpClientHandlerPostfix(ref HttpMessageHandler __result)
        {
            var options = Plugin.Instance.MainOptionsStore.PluginOptions.NetworkOptions;
            var proxyStatus = options.ProxyServerStatus.Status;
            var ignoreCertificateValidation = options.IgnoreCertificateValidation;

            if (Uri.TryCreate(options.ProxyServerUrl, UriKind.Absolute, out var proxyUri) &&
                proxyStatus == ItemStatus.Succeeded && TryParseProxyUrl(options.ProxyServerUrl, out var schema,
                    out var host, out var port, out var username, out var password))
            {
                var proxy = new WebProxy(proxyUri)
                {
                    BypassProxyOnLocal = true,
                    BypassList = BypassAddressList,
                    Credentials = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                        ? new NetworkCredential(username, password)
                        : null
                };

                if (__result is HttpClientHandler httpClientHandler)
                {
                    httpClientHandler.Proxy = proxy;
                    httpClientHandler.UseProxy = true;
                    if (ignoreCertificateValidation)
                    {
                        httpClientHandler.ServerCertificateCustomValidationCallback =
                            (httpRequestMessage, cert, chain, sslErrors) => true;
                    }
                }
                else if (__result is SocketsHttpHandler socketsHttpHandler)
                {
                    socketsHttpHandler.Proxy = proxy;
                    socketsHttpHandler.UseProxy = true;
                    if (ignoreCertificateValidation)
                    {
                        socketsHttpHandler.SslOptions.RemoteCertificateValidationCallback =
                            (sender, cert, chain, sslErrors) => true;
                    }
                }
            }
        }
    }
}

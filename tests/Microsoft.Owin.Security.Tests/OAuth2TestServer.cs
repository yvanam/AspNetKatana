﻿using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Owin.Security.Infrastructure;
using Microsoft.Owin.Security.OAuth;
using Microsoft.Owin.Testing;
using Newtonsoft.Json.Linq;
using Owin;
using Shouldly;

namespace Microsoft.Owin.Security.Tests
{
    public class OAuth2TestServer : TestServer
    {
        public OAuthAuthorizationServerOptions Options { get; set; }
        public OAuthAuthorizationServerProvider Provider { get { return Options.Provider as OAuthAuthorizationServerProvider; } }
        public TestClock Clock { get { return Options.SystemClock as TestClock; } }
        public Func<IOwinContext, Task> OnAuthorizeEndpoint { get; set; }
        public Func<IOwinContext, Task> OnTestpathEndpoint { get; set; }

        public OAuth2TestServer(Action<OAuth2TestServer> configure = null)
        {
            Options = new OAuthAuthorizationServerOptions
            {
                AuthorizeEndpointPath = "/authorize",
                TokenEndpointPath = "/token",
                Provider = new OAuthAuthorizationServerProvider
                {
                    OnValidateClientCredentials = async ctx =>
                    {
                        if (ctx.ClientId == "alpha")
                        {
                            ctx.ClientFound("beta", "http://gamma.com/return");
                        }
                    }
                },
                AuthenticationCodeProvider = new InMemorySingleUseReferenceProvider(),
                SystemClock = new TestClock()
            };
            if (configure != null)
            {
                configure(this);
            }
            Open(app =>
            {
                app.Properties["host.AppName"] = "Microsoft.Owin.Security.Tests";
                app.UseOAuthAuthorizationServer(Options);
                app.UseHandler(async (ctx, next) =>
                {
                    if (ctx.Request.Path == Options.AuthorizeEndpointPath && OnAuthorizeEndpoint != null)
                    {
                        await OnAuthorizeEndpoint(ctx);
                    }
                    else if (ctx.Request.Path == "/testpath" && OnTestpathEndpoint != null)
                    {
                        await OnTestpathEndpoint(ctx);
                    }
                    else
                    {
                        await next();
                    }
                });
            });
        }

        public async Task<Transaction> SendAsync(
            string uri,
            string cookieHeader = null,
            string postBody = null,
            AuthenticationHeaderValue authenticateHeader = null)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                request.Headers.Add("Cookie", cookieHeader);
            }
            if (authenticateHeader != null)
            {
                request.Headers.Authorization = authenticateHeader;
            }
            if (!string.IsNullOrEmpty(postBody))
            {
                request.Method = HttpMethod.Post;
                request.Content = new StringContent(postBody, Encoding.UTF8, "application/x-www-form-urlencoded");
            }

            var transaction = new Transaction
            {
                Request = request,
                Response = await HttpClient.SendAsync(request),
            };
            if (transaction.Response.Headers.Contains("Set-Cookie"))
            {
                transaction.SetCookie = transaction.Response.Headers.GetValues("Set-Cookie").SingleOrDefault();
            }
            if (!string.IsNullOrEmpty(transaction.SetCookie))
            {
                transaction.CookieNameValue = transaction.SetCookie.Split(new[] { ';' }, 2).First();
            }
            transaction.ResponseText = await transaction.Response.Content.ReadAsStringAsync();

            if (transaction.Response.Content != null &&
                transaction.Response.Content.Headers.ContentType != null &&
                transaction.Response.Content.Headers.ContentType.MediaType == "text/xml")
            {
                transaction.ResponseElement = XElement.Parse(transaction.ResponseText);
            }

            if (transaction.Response.Content != null &&
                transaction.Response.Content.Headers.ContentType != null &&
                transaction.Response.Content.Headers.ContentType.MediaType == "application/json")
            {
                transaction.ResponseToken = JToken.Parse(transaction.ResponseText);
            }

            return transaction;
        }

        public class InMemorySingleUseReferenceProvider : AuthenticationTokenProvider
        {
            readonly ConcurrentDictionary<string, string> _database = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

            public override void Create(AuthenticationTokenCreateContext context)
            {
                string tokenValue = Guid.NewGuid().ToString("n");

                _database[tokenValue] = context.SerializeTicket();

                context.SetToken(tokenValue);
            }

            public override void Receive(AuthenticationTokenReceiveContext context)
            {
                string value;
                if (_database.TryRemove(context.Token, out value))
                {
                    context.DeserializeTicket(value);
                }
            }
        }

        public class Transaction
        {
            public HttpRequestMessage Request { get; set; }
            public HttpResponseMessage Response { get; set; }

            public string SetCookie { get; set; }
            public string CookieNameValue { get; set; }

            public string ResponseText { get; set; }
            public XElement ResponseElement { get; set; }
            public JToken ResponseToken { get; set; }

            public NameValueCollection ParseRedirectQueryString()
            {
                Response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
                Response.Headers.Location.Query.ShouldStartWith("?");
                var querystring = Response.Headers.Location.Query.Substring(1);
                var nvc = new NameValueCollection();
                foreach (var pair in querystring
                    .Split(new[] { '?' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Split(new[] { '=' }, 2).Select(Uri.UnescapeDataString)))
                {
                    if (pair.Count() == 2)
                    {
                        nvc.Add(pair.First(), pair.Last());
                    }
                }
                return nvc;
            }
        }
    }
}
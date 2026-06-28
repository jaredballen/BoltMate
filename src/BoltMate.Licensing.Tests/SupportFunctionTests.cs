using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BoltMate.LicenseApi.Configuration;
using BoltMate.LicenseApi.Functions;
using BoltMate.LicenseApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BoltMate.Licensing.Tests;

public sealed class SupportFunctionTests
{
    [Fact]
    public async Task Json_anonymous_with_email_and_message_accepted()
    {
        var (fn, fakes) = Build();
        var req = JsonRequest(new
        {
            email = "anon@example.com",
            name = "Anon",
            subject = "Help",
            message = "Things broke."
        });

        var result = await fn.Run(req, default);

        Assert.IsType<AcceptedResult>(result);
        var ticket = Assert.Single(fakes.Sink.Tickets);
        Assert.Equal("anon@example.com", ticket.FromEmail);
        Assert.Equal("Help", ticket.Subject);
        Assert.Null(ticket.BundleUrl);
        Assert.Equal("anonymous", ticket.Source);
    }

    [Fact]
    public async Task Json_without_email_rejected()
    {
        var (fn, _) = Build();
        var req = JsonRequest(new { message = "hi" });

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.BadRequest, obj.StatusCode);
    }

    [Fact]
    public async Task Json_without_message_rejected()
    {
        var (fn, _) = Build();
        var req = JsonRequest(new { email = "a@b.com" });

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.BadRequest, obj.StatusCode);
    }

    [Fact]
    public async Task Bearer_overrides_submitted_email()
    {
        var (fn, fakes) = Build();
        fakes.IdTokens.OnValidate = _ => new ValidatedIdToken("oauth-sub-1", "real@example.com", "Real");
        var req = JsonRequest(new { email = "fake@example.com", message = "hi" }, bearer: "token");

        var result = await fn.Run(req, default);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal("real@example.com", fakes.Sink.Tickets[0].FromEmail);
        Assert.Equal("authenticated", fakes.Sink.Tickets[0].Source);
    }

    [Fact]
    public async Task Multipart_with_bundle_uploads_and_attaches_url()
    {
        var (fn, fakes) = Build();
        var bundleBytes = Encoding.UTF8.GetBytes("ZIPDATA");
        var req = MultipartRequest(
            fields: new Dictionary<string, string>
            {
                ["email"] = "anon@example.com",
                ["message"] = "logs attached",
                ["source"] = "desktop-app",
            },
            fileFieldName: "bundle",
            fileName: "boltmate-logs.zip",
            fileContentType: "application/zip",
            fileBytes: bundleBytes);

        var result = await fn.Run(req, default);

        Assert.IsType<AcceptedResult>(result);
        var ticket = Assert.Single(fakes.Sink.Tickets);
        Assert.Equal("anon@example.com", ticket.FromEmail);
        Assert.NotNull(ticket.BundleUrl);
        Assert.Equal(bundleBytes.Length, ticket.BundleSizeBytes);
        Assert.Equal("desktop-app", ticket.Source);
        var upload = Assert.Single(fakes.Bundles.Uploads);
        Assert.Equal("boltmate-logs.zip", upload.FileName);
    }

    [Fact]
    public async Task Oversize_bundle_rejected()
    {
        var (fn, _) = Build(maxMB: 1);
        var bytes = new byte[2 * 1024 * 1024]; // 2 MB > 1 MB cap
        var req = MultipartRequest(
            fields: new Dictionary<string, string> { ["email"] = "a@b.com", ["message"] = "x" },
            fileFieldName: "bundle",
            fileName: "big.zip",
            fileContentType: "application/zip",
            fileBytes: bytes);

        var result = await fn.Run(req, default);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal((int)HttpStatusCode.RequestEntityTooLarge, obj.StatusCode);
    }

    private static (SupportFunction Function, Fakes Fakes) Build(int maxMB = 25)
    {
        var fakes = new Fakes();
        var fn = new SupportFunction(
            fakes.IdTokens, fakes.Bundles, fakes.Sink, fakes.Emails,
            Options.Create(new LicenseApiOptions { SupportBundleMaxSizeMB = maxMB }),
            NullLogger<SupportFunction>.Instance);
        return (fn, fakes);
    }

    private static HttpRequest JsonRequest(object body, string? bearer = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        var json = JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Request.ContentType = "application/json";
        if (bearer is not null) ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        return ctx.Request;
    }

    private static HttpRequest MultipartRequest(
        Dictionary<string, string> fields,
        string fileFieldName,
        string fileName,
        string fileContentType,
        byte[] fileBytes)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        var boundary = "----TestBoundary" + System.Guid.NewGuid().ToString("N");
        var ms = new MemoryStream();
        void WriteAscii(string s) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); }

        foreach (var (k, v) in fields)
        {
            WriteAscii($"--{boundary}\r\n");
            WriteAscii($"Content-Disposition: form-data; name=\"{k}\"\r\n\r\n");
            WriteAscii($"{v}\r\n");
        }
        WriteAscii($"--{boundary}\r\n");
        WriteAscii($"Content-Disposition: form-data; name=\"{fileFieldName}\"; filename=\"{fileName}\"\r\n");
        WriteAscii($"Content-Type: {fileContentType}\r\n\r\n");
        ms.Write(fileBytes, 0, fileBytes.Length);
        WriteAscii("\r\n");
        WriteAscii($"--{boundary}--\r\n");

        ms.Position = 0;
        ctx.Request.Body = ms;
        ctx.Request.ContentLength = ms.Length;
        ctx.Request.ContentType = $"multipart/form-data; boundary={boundary}";
        return ctx.Request;
    }

    internal sealed class Fakes
    {
        public FakeIdTokenValidator IdTokens { get; } = new();
        public FakeSupportBundleStore Bundles { get; } = new();
        public FakeSupportTicketSink Sink { get; } = new();
        public FakeEmailNotifier Emails { get; } = new();
    }
}

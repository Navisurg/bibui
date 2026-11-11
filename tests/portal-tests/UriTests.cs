using System.Net.Sockets;
using Bibui.Platform.Portal;
using Bibui.Platform.Portal.DBus;

namespace Bibui.PortalTests;

/// <summary>The portal's file URIs as local paths, and the session bus's address as sockets.</summary>
internal static class UriTests
{
    public static void Register(Harness t)
    {
        t.Run("uri: file URIs decode to local paths", () =>
        {
            Assert.Eq("/home/a/My Notes.md", FileUris.ToLocalPath("file:///home/a/My%20Notes.md"), "space");
            Assert.Eq("/home/a/Mød/✎.txt", FileUris.ToLocalPath("file:///home/a/M%C3%B8d/%E2%9C%8E.txt"), "UTF-8 escapes");
            Assert.Eq("/home/a/Mød", FileUris.ToLocalPath("file:///home/a/Mød"), "unescaped UTF-8, as some portals send");
            Assert.Eq("/tmp/a#1?.txt", FileUris.ToLocalPath("file:///tmp/a%231%3F.txt"), "escaped # and ?");
            Assert.Eq("/tmp/100%", FileUris.ToLocalPath("file:///tmp/100%25"), "escaped %");
            Assert.Eq("/tmp/x", FileUris.ToLocalPath("file://localhost/tmp/x"), "localhost");
            Assert.Eq("/tmp/x", FileUris.ToLocalPath("FILE:///tmp/x"), "scheme case");
            Assert.Eq("/run/user/1000/doc/1a2b/x.png", FileUris.ToLocalPath("file:///run/user/1000/doc/1a2b/x.png"), "a document-portal path");
            Assert.Eq("/tmp/a", FileUris.ToLocalPath("file:///tmp/a#fragment"), "a bare # ends the path");
            Assert.Eq("/tmp/aé", FileUris.ToLocalPath("file:///tmp/a%c3%a9"), "lower-case hex");
            Assert.Eq("/tmp/✎", FileUris.ToLocalPath("file:///tmp/%e2%9c%8e"), "lower-case hex in both digits");
        });

        t.Run("uri: what is not a local file is refused", () =>
        {
            Assert.Eq(null, FileUris.ToLocalPath("https://example.com/x"), "another scheme");
            Assert.Eq(null, FileUris.ToLocalPath("file://otherhost/tmp/x"), "another host");
            Assert.Eq(null, FileUris.ToLocalPath("file://"), "no path");
            Assert.Eq(null, FileUris.ToLocalPath("file:///tmp/%2"), "short escape");
            Assert.Eq(null, FileUris.ToLocalPath("file:///tmp/%zz"), "not hex");
            Assert.Eq(null, FileUris.ToLocalPath("file:///tmp/a%00b"), "NUL");
            Assert.Eq(null, FileUris.ToLocalPath("file:///tmp/%FF"), "not UTF-8");
            Assert.Eq(null, FileUris.ToLocalPath("/tmp/x"), "a bare path");
        });

        t.Run("uri: a Response with a non-local URI keeps the local ones and says so", () =>
        {
            var r = FileChooser.ReadResponse(ChooserTests.Response(0, "file:///tmp/a", "sftp://host/b"));
            Assert.Eq(FileChooserOutcome.Chosen, r.Outcome, "outcome");
            Assert.Eq("/tmp/a", string.Join("|", r.Paths), "paths");
            Assert.Contains(r.Reason ?? "", "sftp://host/b");
            var none = FileChooser.ReadResponse(ChooserTests.Response(0, "sftp://host/b"));
            Assert.Eq(FileChooserOutcome.Failed, none.Outcome, "nothing local");
        });

        t.Run("bus address: unix sockets, escapes, and what is skipped", () =>
        {
            var e = SessionBus.Endpoints("unix:path=/run/user/1000/bus,guid=abc");
            Assert.Eq(1, e.Count, "one");
            Assert.Eq("/run/user/1000/bus", e[0].ToString(), "path");
            Assert.Eq("/tmp/a b", SessionBus.Endpoints("unix:path=/tmp/a%20b")[0].ToString(), "escaped");
            var many = SessionBus.Endpoints("tcp:host=localhost,port=1;unix:abstract=/tmp/dbus-x;unix:path=/b");
            Assert.Eq(2, many.Count, "tcp skipped");
            Assert.True(many[0].ToString()!.StartsWith('@') || many[0].ToString()!.StartsWith('\0'), $"abstract: {many[0]}");
            Assert.Eq("/b", many[1].ToString(), "then the path");
            Assert.Eq(0, SessionBus.Endpoints("unix:tmpdir=/tmp").Count, "a listening address only");
            Assert.Eq(0, SessionBus.Endpoints("unix:path=/tmp/%zz").Count, "a bad escape");
            Assert.Eq(0, SessionBus.Endpoints("").Count, "empty");
        });
    }
}

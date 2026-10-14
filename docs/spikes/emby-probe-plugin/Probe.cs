using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace LabelMakerProbe;

public class Plugin : BasePlugin<BasePluginConfiguration>, IServerEntryPoint
{
    public static Plugin Instance;
    public static string ResolveLog = "none";
    static Plugin()
    {
        ResolveLog = "embedded handler registered";
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) => name.Name == "TagLibSharp" ? LoadEmbedded(ctx) : null;
    }
    private static Assembly LoadEmbedded(System.Runtime.Loader.AssemblyLoadContext ctx)
    {
        using var s = typeof(Plugin).Assembly.GetManifestResourceStream("TagLibSharp.dll");
        return ctx.LoadFromStream(s);
    }
    public ILibraryManager Library; public ILibraryMonitor Monitor; public ISessionManager Sessions;
    public override string Name => "Label Maker Probe";
    public override string Description => "Phase 0 spike (temporary).";
    public override Guid Id => Guid.Parse("0a5c0c58-6f3c-4a43-9d58-3d0e6f3f0b11");
    public Plugin(IApplicationPaths p, IXmlSerializer x, ILibraryManager lib, ILibraryMonitor mon, ISessionManager ses) : base(p, x)
    { Instance = this; Library = lib; Monitor = mon; Sessions = ses; }
    public void Run() { }
    public void Dispose() { }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMakerProbe/Run", "GET")]
public class ProbeRequest : IReturn<object> { public string Dir { get; set; } }

public class ProbeService : IService
{
    public object Get(ProbeRequest r)
    {
        var res = new Dictionary<string, object>();
        res["resolve"] = Plugin.ResolveLog;
        res["user"] = Environment.UserDomainName + "\\" + Environment.UserName;
        res["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        try
        {
            var a = typeof(TagLib.File).Assembly;
            res["taglib"] = a.GetName().Version + " @ " + a.Location;
            var dst = Path.Combine(r.Dir, "probe-" + Guid.NewGuid().ToString("N") + ".flac");
            File.Copy(Path.Combine(r.Dir, "t.flac"), dst);
            using (var f = TagLib.File.Create(dst)) { f.Tag.Title = "Probe OK"; f.Tag.MusicBrainzReleaseId = "b25a1e5d-3247-41ea-9920-f93c58a6e479"; f.Save(); }
            using (var f = TagLib.File.Create(dst)) res["roundtrip"] = f.Tag.Title + " / " + f.Tag.MusicBrainzReleaseId;
            File.Delete(dst);
        }
        catch (Exception e) { res["taglibError"] = e.GetType().Name + ": " + e.Message; }
        try
        {
            res["virtualFolders"] = Plugin.Instance.Library.GetVirtualFolders()
                .Select(v => v.Name + " [" + v.CollectionType + "] -> " + string.Join(" ; ", v.Locations ?? new string[0])).ToArray();
        }
        catch (Exception e) { res["virtualFoldersError"] = e.Message; }
        res["libraryMonitorInjected"] = Plugin.Instance.Monitor != null;
        try
        {
            Plugin.Instance.Sessions.SendMessageToAdminSessions("LabelMakerProbe.Test", new { Count = 3 }, CancellationToken.None).GetAwaiter().GetResult();
            res["adminPush"] = "sent (no exception)";
        }
        catch (Exception e) { res["adminPushError"] = e.Message; }
        return res;
    }
}

[Authenticated(Roles = "Admin")]
[Route("/LabelMakerProbe/Scan", "GET")]
public class ScanRequest : IReturn<object> { public string Path { get; set; } public bool Begin { get; set; } }

public class ScanService : IService
{
    public object Get(ScanRequest r)
    {
        var m = Plugin.Instance.Monitor;
        if (r.Begin) m.ReportFileSystemChangeBeginning(r.Path);
        else m.ReportFileSystemChangeComplete(r.Path, true);
        return new { ok = true, r.Path, r.Begin };
    }
}

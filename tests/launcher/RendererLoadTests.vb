Imports DiRT3VR
Imports System.IO

Module RendererLoadTests
    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim proxy = "C:\Games\DiRT 3 Complete Edition\d3d11.dll", windows = "C:\Windows\SysWOW64\d3d11.dll"
        check(RendererLoad.Classify(proxy, True, True, {windows, proxy.ToUpperInvariant()}, False) = RendererLoad.Loaded, "DiRT3VR's d3d11.dll among the loaded modules means VR can start")
        check(RendererLoad.Classify(proxy, False, False, {windows}, False) = RendererLoad.ProxyMissing, "a removed proxy is named first")
        check(RendererLoad.Classify(proxy, True, False, {windows}, True) = RendererLoad.ProxyChanged, "a replaced proxy is named before the Windows rule")
        check(RendererLoad.Classify(proxy, True, True, {windows}, True) = RendererLoad.PreferSystem32Policy, "the 'prefer system32 images' rule is named when it applied")
        check(RendererLoad.Classify(proxy, True, True, {windows}, False) = RendererLoad.WindowsD3d11Loaded AndAlso RendererLoad.Classify(proxy, True, True, Array.Empty(Of String)(), Nothing) = RendererLoad.WindowsD3d11Loaded, "otherwise Windows' own d3d11.dll loaded instead")
        check(RendererLoad.Classify(proxy, True, True, Nothing, Nothing) = RendererLoad.NotInspected, "unreadable modules prove nothing")
        check(RendererLoad.Message(RendererLoad.Loaded) Is Nothing AndAlso RendererLoad.Message(RendererLoad.NotInspected) Is Nothing, "no message without a proven failure")
        check(RendererLoad.Message(RendererLoad.ProxyMissing).Contains("antivirus") AndAlso RendererLoad.Message(RendererLoad.ProxyChanged).Contains("another mod") AndAlso
              RendererLoad.Message(RendererLoad.PreferSystem32Policy).Contains("prefer system32 images") AndAlso RendererLoad.Message(RendererLoad.WindowsD3d11Loaded).Contains("Visual C++ 2015-2022 Redistributable (x86)"), "each cause comes with a fix")

        Dim options(7) As Byte
        check(Not RendererLoad.PreferSystem32Option(options) AndAlso Not RendererLoad.PreferSystem32Option(Nothing) AndAlso Not RendererLoad.PreferSystem32Option({1, 2}), "no exploit-protection setting means no rule")
        options(7) = &H10
        check(RendererLoad.PreferSystem32Option(options), "MitigationOptions bits 60-61 = 1 force the rule on")
        options(7) = &H20
        check(Not RendererLoad.PreferSystem32Option(options), "bits 60-61 = 2 turn the rule off")
        check(Not RendererLoad.ExploitProtectionForces(Path.Combine(root, "no-such-dirt3vr-test.exe")), "a program without exploit-protection settings is not blocked")

        Dim own = RendererLoad.ModulePaths(Environment.ProcessId, "kernel32.dll")
        check(own IsNot Nothing AndAlso own.Length = 1 AndAlso own(0).EndsWith("kernel32.dll", StringComparison.OrdinalIgnoreCase), "loaded modules of a process can be listed")
        Dim policy = RendererLoad.PrefersSystem32Images(Environment.ProcessId)
        check(Not policy.HasValue OrElse Not policy.Value, "the test runner itself is not under the rule (" & If(policy.HasValue, policy.Value.ToString(), "unknown") & ")")

        ' Diagnose against this test process, which has not loaded the fake game folder's d3d11.dll.
        Dim package = Path.Combine(root, "renderer-package")
        Directory.CreateDirectory(Path.Combine(package, "payload"))
        File.WriteAllText(Path.Combine(package, "payload", "d3d11.dll"), "DiRT3VR proxy")
        Dim ctx As New InstallContext(Path.Combine(root, "renderer-game"), Path.Combine(root, "renderer-users"), Path.Combine(root, "renderer-game", "graphics.xml"), package)
        Directory.CreateDirectory(ctx.GameRoot)
        Dim logs = Path.Combine(root, "renderer-logs")
        Directory.CreateDirectory(logs)
        Dim result = RendererLoad.Diagnose(ctx, Environment.ProcessId, logs)
        check(result.Reason = RendererLoad.ProxyMissing AndAlso Not result.RendererLoaded AndAlso result.Message.Contains("removed") AndAlso File.Exists(Path.Combine(logs, "renderer-load.json")), "a missing proxy is diagnosed and recorded in the session logs")
        File.WriteAllText(Path.Combine(ctx.GameRoot, "d3d11.dll"), "another mod")
        check(RendererLoad.Diagnose(ctx, Environment.ProcessId, Nothing).Reason = RendererLoad.ProxyChanged, "a different d3d11.dll in the game folder is diagnosed")
        File.WriteAllText(Path.Combine(ctx.GameRoot, "d3d11.dll"), "DiRT3VR proxy")
        Dim fallback = RendererLoad.Diagnose(ctx, Environment.ProcessId, Nothing).Reason
        check(fallback = RendererLoad.WindowsD3d11Loaded OrElse fallback = RendererLoad.PreferSystem32Policy, "the right proxy that was not loaded points at Windows' loader")
        check(RendererLoad.LaunchBlock(ctx) = "" OrElse RendererLoad.LaunchBlock(ctx).StartsWith("DiRT3VR did not start DiRT 3", StringComparison.Ordinal), "the launch check either passes or explains the block")
    End Sub
End Module

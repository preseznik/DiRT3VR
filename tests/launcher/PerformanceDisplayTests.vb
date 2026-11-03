Imports DiRT3VR
Imports System.IO
Imports System.Text.Json
Imports System.Windows.Forms

' FPS and GPU usage in VR (#4896ffd3): the headset display's settings and environment (Lens's contract:
' DIRT3VR_PERF_DISPLAY and DIRT3VR_PERF_FOLLOW "1"/"0"), and the session's performance-summary.json
' (GRID2VR's schema 1) shown with the last VR session.
Module PerformanceDisplayTests
    Private Const Enabled As String = "XR prototype enabled headset=2400x2600 render_scale=100 scene=2400x2600 game_units_per_metre=1; first tracked focused frame required"

    ' A summary as GRID2VR's renderer writes it (trimmed of the timeline and segment details).
    Private Function Summary(Optional limit As String = "gpu", Optional windows As Integer = 240, Optional share As String = "0.02", Optional dipLimit As String = "gpu", Optional schema As Integer = 1) As String
        Return "{""schema"":" & schema & ",""final"":true,""adapter"":{""name"":""NVIDIA GeForce RTX 3070"",""vendorId"":4318,""deviceId"":9348,""dedicatedMb"":8031}," &
               """headset"":{""width"":2400,""height"":2600,""scale"":100},""displayPeriodMs"":11.111,""targetFps"":90,""stereoFrames"":21000,""screenFrames"":300," &
               """fps"":{""windows"":" & windows & ",""p5"":61.2,""p50"":72.4,""p95"":80.1,""mean"":71.9},""blockedMs"":{""count"":21000,""p50"":0.4,""p95"":1.2,""p99"":2,""max"":9}," &
               """gpuPercent"":{""samples"":240,""p50"":97,""p95"":99},""limit"":""" & limit & """," &
               """dips"":{""threshold"":0.95,""slowWindows"":5,""share"":" & share & ",""gpu"":5,""cpu"":0,""mixed"":0,""limit"":""" & dipLimit & """}}"
    End Function

    Private Function WithoutLastBrace(json As String) As String
        Return json.Substring(0, json.Length - 1)
    End Function

    Private Function Measured(json As String) As MeasuredPerformance
        Using document = JsonDocument.Parse(json)
            Return MeasuredPerformance.Parse(document.RootElement)
        End Using
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "perf", name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "perf-users", name), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' Settings and the session environment: off and fixed by default.
        Dim defaults As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}
        check(Not defaults.PerfDisplay AndAlso Not defaults.PerfFollowView, "the performance display is off by default")
        Dim ctx = Context(root, "env")
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Dim plain = InteractiveSession.StartInfo(ctx, runtime, logs, defaults, headset)
        check(plain.Environment("DIRT3VR_PERF_DISPLAY") = "0" AndAlso plain.Environment("DIRT3VR_PERF_FOLLOW") = "0", "a VR session passes 0 and 0 by default")
        Dim shown = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .PerfDisplay = True, .PerfFollowView = True}, headset)
        check(shown.Environment("DIRT3VR_PERF_DISPLAY") = "1" AndAlso shown.Environment("DIRT3VR_PERF_FOLLOW") = "1", "turned on and following the view: 1 and 1")
        Dim applied = ProfileValues.Capture(defaults).ApplyTo(New LauncherSettings With {.PerfDisplay = True, .PerfFollowView = True})
        check(applied.PerfDisplay AndAlso applied.PerfFollowView, "loading a settings profile keeps this PC's choice")

        ' The summary.
        Dim gpu = Measured(Summary())
        check(gpu.Usable AndAlso gpu.TargetFps = 90 AndAlso gpu.FpsMedian = 72.4 AndAlso gpu.FpsLow = 61.2 AndAlso gpu.Windows = 240 AndAlso gpu.GpuPercent.GetValueOrDefault() = 97 AndAlso
              gpu.Limit = "gpu" AndAlso gpu.Scale = 100 AndAlso gpu.Adapter = "NVIDIA GeForce RTX 3070" AndAlso gpu.BlockedMedian.GetValueOrDefault() = 0.4 AndAlso gpu.DipShare.GetValueOrDefault() = 0.02,
              "GRID2VR's schema 1 is read: rate, median and low FPS, GPU usage, limit, headset, adapter, dips")
        Dim culture = Globalization.CultureInfo.CurrentCulture
        Try
            Globalization.CultureInfo.CurrentCulture = Globalization.CultureInfo.InvariantCulture
            check(gpu.Describe() = "72 FPS median, 61 low (headset 90) · GPU 97% · limited by the GPU", "it reads as FPS, GPU usage and what held it back (" & gpu.Describe() & ")")
            Dim dipping = Measured(Summary("at_target", share:="0.12", dipLimit:="cpu"))
            check(dipping.Dips AndAlso dipping.Describe().EndsWith("mostly holding the headset rate, CPU-limited dips in 12% of seconds", StringComparison.Ordinal), "holding the rate with dips says so (" & dipping.Describe() & ")")
            check(Measured(Summary("at_target", share:="0.01")).Describe().EndsWith("holding the headset rate", StringComparison.Ordinal), "holding the rate")
            check(Measured(Summary(windows:=4)).Describe().StartsWith("too short to measure", StringComparison.Ordinal) AndAlso Measured(Summary("insufficient")).Describe().StartsWith("too short", StringComparison.Ordinal),
                  "a session under 10 seconds of stereo driving is too short to measure")
            ' #2667690f: the 32-bit process's peak address space, when the summary has it (Wrench's "memory" object).
            Dim withMemory = WithoutLastBrace(Summary()) & ",""memory"":{""peakCommittedMb"":2900,""peakAddressMb"":3174,""addressLimitMb"":4096}}"
            check(Measured(withMemory).Describe().EndsWith("limited by the GPU · memory 3.1 of 4.0 GB", StringComparison.Ordinal) AndAlso Measured(withMemory).PeakCommittedMb.GetValueOrDefault() = 2900,
                  "the peak memory follows, as used of available (" & Measured(withMemory).Describe() & ")")
            Dim nearLimit = WithoutLastBrace(Summary()) & ",""memory"":{""peakCommittedMb"":1800,""peakAddressMb"":1900,""addressLimitMb"":2048}}"
            check(Measured(nearLimit).Describe().EndsWith("memory 1.9 of 2.0 GB (close to the limit)", StringComparison.Ordinal), "near the limit it says so")
            Dim noSample = WithoutLastBrace(Summary()) & ",""memory"":{""peakCommittedMb"":null,""peakAddressMb"":null,""addressLimitMb"":null}}"
            check(Not Measured(noSample).Describe().Contains("memory") AndAlso Not Measured(Summary()).Describe().Contains("memory"), "without a sample, or in older summaries, no memory is shown")
        Finally
            Globalization.CultureInfo.CurrentCulture = culture
        End Try
        check(Measured(Summary(schema:=2)) Is Nothing AndAlso Measured("[1,2]") Is Nothing, "another schema or shape is ignored")
        check(Not Measured("{""schema"":1,""fps"":{""p50"":""fast""},""limit"":7}").Usable, "wrong value types count as missing")

        ' The last VR session line includes it; older sessions without the file don't.
        Dim load = Context(root, "load")
        Dim folder = Path.Combine(load.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(folder)
        File.WriteAllText(Path.Combine(folder, "native-4242.log"), Enabled & vbLf & "XR display refresh rate=90.00 Hz" & vbLf & "XR prototype main=1 submitted=1")
        check(LastVrSession.Load(load).Performance Is Nothing AndAlso Not LastVrSession.Load(load).Describe().Contains("FPS"), "a session without a performance summary shows no FPS")
        File.WriteAllText(Path.Combine(folder, MeasuredPerformance.FileName), Summary())
        Dim last = LastVrSession.Load(load)
        check(last.Performance IsNot Nothing AndAlso last.Describe().Contains("FPS median") AndAlso last.Describe().Contains("headset refresh 90 Hz"), "with one, the line adds the measured FPS (" & last.Describe() & ")")
        File.WriteAllText(Path.Combine(folder, MeasuredPerformance.FileName), "{not json")
        check(LastVrSession.Load(load) IsNot Nothing AndAlso LastVrSession.Load(load).Performance Is Nothing, "a damaged summary is ignored, the rest still shows")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim display = DirectCast(form.Controls.Find("PerfDisplay", True).Single(), CheckBox)
            Dim follow = DirectCast(form.Controls.Find("PerfFollowView", True).Single(), CheckBox)
            check(Not display.Checked AndAlso Not follow.Checked AndAlso Not follow.Enabled AndAlso Not form.HasUnsavedSettings(), "off by default; follows view waits for the display")
            display.Checked = True : follow.Checked = True : Application.DoEvents()
            check(follow.Enabled AndAlso form.CurrentSettings().PerfDisplay AndAlso form.CurrentSettings().PerfFollowView AndAlso form.HasUnsavedSettings(), "both are launcher settings")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.PerfDisplay AndAlso saved.PerfFollowView AndAlso Not form.HasUnsavedSettings(), "Save settings keeps them")
            form.Close()
        End Using
    End Sub
End Module

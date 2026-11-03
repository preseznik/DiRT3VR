Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

' Recommend for this PC (#68e16dc9): GRID2VR's measured rules on DiRT3VR's measured session (#4896ffd3),
' only for the VR quality and headset the session ran, and the Graphics → Quality button.
Module GraphicsRecommendationTests
    Private ReadOnly High As New Dictionary(Of String, String)(GraphicsCatalog.Current.Presets.Single(Function(preset) preset.Id = "high").Values)
    Private ReadOnly Ultra As New Dictionary(Of String, String)(GraphicsCatalog.Current.Presets.Single(Function(preset) preset.Id = "ultra").Values)

    Private Function Session(limit As String, Optional median As Double = 72, Optional low As Double = 61, Optional gpu As Double? = 97, Optional scale As Integer = 100,
                             Optional quality As Dictionary(Of String, String) = Nothing, Optional dipShare As Double? = 0.01, Optional dipLimit As String = "gpu",
                             Optional windows As Integer = 240, Optional blocked As Double? = 0.4) As LastVrSession
        Return New LastVrSession With {.VrQuality = GraphicsCatalog.Current.Normalize(If(quality, High)), .Performance = New MeasuredPerformance With {
            .TargetFps = 90, .FpsMedian = median, .FpsLow = low, .Windows = windows, .GpuPercent = gpu, .Limit = limit, .HeadsetWidth = 2400, .HeadsetHeight = 2600,
            .Scale = scale, .DipShare = dipShare, .DipLimit = dipLimit, .BlockedMedian = blocked}}
    End Function

    Private Function Current(Optional quality As Dictionary(Of String, String) = Nothing) As LauncherSettings
        Return New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .VrGraphicsQuality = If(quality, High)}
    End Function

    Private Function Recommend(last As LastVrSession, Optional settings As LauncherSettings = Nothing) As GraphicsRecommendation.Result
        Return GraphicsRecommendation.Recommend(last, If(settings, Current()), 2400, 2600)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' The session's VR quality: each VR choice, else the desktop one.
        Dim merged = GraphicsRecommendation.VrQuality(New LauncherSettings With {.GraphicsQuality = New Dictionary(Of String, String) From {{"Shadows", "low"}, {"Crowd", "high"}},
                                                                                 .VrGraphicsQuality = New Dictionary(Of String, String) From {{"Crowd", "low"}}})
        check(merged.Count = 2 AndAlso merged("Shadows") = "low" AndAlso merged("Crowd") = "low", "the session's VR quality is the VR choice, else the desktop one")

        ' Nothing to go on: no change, and why.
        check(Recommend(Nothing).Preset = GraphicsRecommendation.Unchanged AndAlso Recommend(Nothing).Reason.StartsWith("No measured VR session", StringComparison.Ordinal), "no measured session: no change")
        check(Recommend(Session("gpu", windows:=5)).Preset = GraphicsRecommendation.Unchanged, "too short: no change")
        check(GraphicsRecommendation.Recommend(Session("gpu"), Current(), 1832, 1920).Reason.Contains("another headset"), "another headset: no change")
        check(Recommend(Session("gpu"), Current(Ultra)).Reason.Contains("other VR quality") AndAlso Recommend(New LastVrSession With {.Performance = Session("gpu").Performance}).Preset = GraphicsRecommendation.Unchanged,
              "other VR quality, or a session that didn't record its quality: no change")

        ' The measured rules.
        Dim gpuLimited = Recommend(Session("gpu", median:=72))
        check(gpuLimited.Preset = GraphicsRecommendation.Keep AndAlso gpuLimited.Scale = 85 AndAlso gpuLimited.Reason.Contains("GPU was the limit"), "GPU-limited at 72 of 90: render scale 100% → 85% (" & gpuLimited.Scale & ")")
        check(Recommend(Session("gpu", median:=88)).Scale = 95, "only just below the rate: at least 5% lower")
        Dim heavy = Recommend(Session("gpu", median:=30, scale:=70))
        check(heavy.Preset = GraphicsRecommendation.Potato AndAlso heavy.Scale = 70, "too heavy even at 70%: Potato / Low-End")
        Dim onUltra = Recommend(Session("gpu", quality:=Ultra), Current(Ultra))
        check(onUltra.Preset = GraphicsRecommendation.Balanced AndAlso onUltra.Scale = 100, "GPU-limited on Ultra: VR Balanced at the same scale")
        Dim cpu = Recommend(Session("cpu", gpu:=60))
        check(cpu.Preset = GraphicsRecommendation.Keep AndAlso cpu.Scale = 100 AndAlso cpu.Reason.Contains("won't help"), "CPU-limited: the scale stays, with what helps instead")
        Dim headroom = Recommend(Session("at_target", median:=90, low:=88, gpu:=55, scale:=80))
        check(headroom.Scale = 90 AndAlso headroom.Reason.Contains("goes up 10%"), "holding the rate with a GPU at 55%: up 10%")
        check(Recommend(Session("at_target", median:=90, low:=88, gpu:=55, scale:=100)).Scale = 100, "never above 100% by itself")
        check(Recommend(Session("at_target", median:=90, low:=88, gpu:=85)).Reason.Contains("Keep them"), "holding the rate with a busy GPU: keep")
        Dim dips = Recommend(Session("at_target", median:=90, low:=70, dipShare:=0.12, dipLimit:="gpu"))
        check(dips.Scale = 90 AndAlso dips.Reason.Contains("12% of seconds"), "GPU-limited dips: lower for the slow seconds (" & dips.Scale & ")")
        check(Recommend(Session("at_target", median:=90, low:=70, dipShare:=0.12, dipLimit:="cpu")).Scale = 100, "CPU-limited dips: the scale stays")
        check(Recommend(Session("mixed", median:=80)).Scale = 90, "below the rate without one clear limit: down 10%")

        ' The last session records and reads back its VR quality.
        Dim game = Path.Combine(root, "recommend", "game")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "recommend", "users"), Path.Combine(game, "graphics.xml"), root)
        Dim folder = Path.Combine(ctx.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(folder)
        File.WriteAllText(Path.Combine(folder, "native-1.log"), "XR prototype enabled headset=2400x2600 render_scale=100 scene=2400x2600 game_units_per_metre=1" & vbLf & "XR prototype main=1 submitted=1")
        SafeFiles.SaveJson(Path.Combine(folder, "session.json"), New With {.schema = 1, .mode = "experimental-interactive", .vrQuality = GraphicsRecommendation.VrQuality(Current())})
        check(LastVrSession.Load(ctx).VrQuality IsNot Nothing AndAlso LastVrSession.Load(ctx).VrQuality.Count = High.Count, "the session's recorded VR quality is read back")
        SafeFiles.SaveJson(Path.Combine(folder, "session.json"), New With {.schema = 1, .mode = "experimental-interactive", .vrQuality = New Dictionary(Of String, String) From {{"Nope", "x"}}})
        check(LastVrSession.Load(ctx).VrQuality Is Nothing, "an unknown recorded option counts as not recorded")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim game = Path.Combine(root, "recommend-ui", "game")
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "recommend-ui", "users"), Path.Combine(game, "graphics.xml"), root)
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .VrGraphicsQuality = High}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim notice = form.Controls.Find("Notice", True).Single()
            Dim undo = DirectCast(form.Controls.Find("UndoGraphicsPreset", True).Single(), Button)
            Dim recommend = DirectCast(form.Controls.Find("RecommendGraphics", True).Single(), Button)
            Dim tabs = DirectCast(form.Controls.Find("LauncherTabs", True).Single(), TabControl)
            tabs.SelectedTab = tabs.TabPages.Cast(Of TabPage)().Single(Function(page) page.Contains(recommend)) : Application.DoEvents()
            recommend.PerformClick()
            check(notice.Text.StartsWith("No measured VR session", StringComparison.Ordinal) AndAlso form.CurrentSettings().VrRenderScale = 100 AndAlso Not undo.Enabled, "without a measured session nothing changes and the notice says why")

            ' A measured, GPU-limited session with the same VR quality.
            Dim folder = Path.Combine(ctx.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(folder)
            File.WriteAllText(Path.Combine(folder, "native-1.log"), "XR prototype enabled headset=2400x2600 render_scale=100 scene=2400x2600 game_units_per_metre=1" & vbLf & "XR prototype main=1 submitted=1")
            SafeFiles.SaveJson(Path.Combine(folder, "session.json"), New With {.schema = 1, .mode = "experimental-interactive", .vrQuality = GraphicsRecommendation.VrQuality(LauncherSettings.Load(ctx))})
            File.WriteAllText(Path.Combine(folder, MeasuredPerformance.FileName),
                "{""schema"":1,""adapter"":{""name"":""Test GPU""},""headset"":{""width"":2400,""height"":2600,""scale"":100},""targetFps"":90,""fps"":{""windows"":120,""p5"":60,""p50"":72}," &
                """gpuPercent"":{""p50"":97},""blockedMs"":{""p50"":0.3},""limit"":""gpu"",""dips"":{""share"":0.02,""limit"":""gpu""}}")
            recommend.PerformClick()
            check(form.CurrentSettings().VrRenderScale = 85 AndAlso undo.Enabled AndAlso notice.Text.Contains("GPU was the limit") AndAlso form.HasUnsavedSettings(),
                  "a GPU-limited session lowers the render scale on the form, with Undo, unsaved (" & form.CurrentSettings().VrRenderScale & ")")
            undo.PerformClick()
            check(form.CurrentSettings().VrRenderScale = 100, "Undo preset brings the scale back")
            form.Close()
        End Using
    End Sub
End Module

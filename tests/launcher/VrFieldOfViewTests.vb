Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

' VR field of view and helmet view (#16a86c75, #47d16d40; Lens's contract): 70–100% of each eye's view, the session's
' scene size cropped the same way after the render scale, DIRT3VR_VR_FOV_WIDTH / _HEIGHT under 100, and the session
' records it for Recommend for this PC.
Module VrFieldOfViewTests
    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Settings(Optional fov As Integer = 100, Optional helmet As Boolean = False) As LauncherSettings
        Return New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .VrFieldOfView = fov, .VrHelmetView = helmet}
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "vr-fov", name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, "vr-fov-users", name), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' The settings.
        check(Settings().VrFieldOfView = 100 AndAlso Not Settings().VrHelmetView AndAlso Settings().VrFovWidth() = 100 AndAlso Settings().VrFovHeight() = 100, "the full view by default")
        Settings(70).Validate()
        check(Refused(Sub() Settings(69).Validate()) AndAlso Refused(Sub() Settings(101).Validate()), "70% to 100% only")
        check(Settings(90).VrFovWidth() = 90 AndAlso Settings(90).VrFovHeight() = 90 AndAlso Settings(85, True).VrFovWidth() = 100 AndAlso Settings(85, True).VrFovHeight() = 85,
              "Field of view crops both ways; helmet view only the top and bottom")
        check(ProfileValues.Capture(Settings()).ApplyTo(Settings(80, True)).VrFieldOfView = 80 AndAlso LauncherDefaults.Graphics(Settings(80, True)).VrFieldOfView = 100 AndAlso Not LauncherDefaults.Graphics(Settings(80, True)).VrHelmetView,
              "a settings profile keeps this PC's choice; Restore graphics defaults puts back the full view")

        ' The scene size: each axis times its share, after the render scale.
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        check(headset.CroppedSceneSize(100, 90, 90) = New Drawing.Size(2160, 2340) AndAlso headset.CroppedSceneSize(100, 100, 85) = New Drawing.Size(2400, 2210) AndAlso
              headset.CroppedSceneSize(50, 90, 90) = New Drawing.Size(1080, 1170), "90% renders 2160 × 2340 of 2400 × 2600; helmet 85% keeps the width; after a 50% render scale too")
        check(Refused(Sub() headset.CroppedSceneSize(100, 60, 100)), "under 70% is refused")
        check(headset.Summary(100, 90, 90).Contains("Render: 2160 × 2340 per eye at 100% with a 90% field of view") AndAlso headset.Summary(100, 100, 85).Contains("with a 100% × 85% field of view") AndAlso
              Not headset.Summary(100).Contains("field of view"), "the headset summary shows the cropped size (" & headset.Summary(100, 90, 90) & ")")

        ' The session environment (Lens's contract).
        Dim ctx = Context(root, "env")
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Environment.SetEnvironmentVariable("DIRT3VR_VR_FOV_WIDTH", "80")
        Try
            Dim full = InteractiveSession.StartInfo(ctx, runtime, logs, Settings(), headset)
            check(Not full.Environment.ContainsKey("DIRT3VR_VR_FOV_WIDTH") AndAlso Not full.Environment.ContainsKey("DIRT3VR_VR_FOV_HEIGHT"), "the full view passes nothing, even if inherited")
        Finally
            Environment.SetEnvironmentVariable("DIRT3VR_VR_FOV_WIDTH", Nothing)
        End Try
        Dim cropped = InteractiveSession.StartInfo(ctx, runtime, logs, Settings(90), headset)
        check(cropped.Environment("DIRT3VR_VR_FOV_WIDTH") = "90" AndAlso cropped.Environment("DIRT3VR_VR_FOV_HEIGHT") = "90", "90% passes 90 and 90")
        Dim helmet = InteractiveSession.StartInfo(ctx, runtime, logs, Settings(85, True), headset)
        check(Not helmet.Environment.ContainsKey("DIRT3VR_VR_FOV_WIDTH") AndAlso helmet.Environment("DIRT3VR_VR_FOV_HEIGHT") = "85", "helmet view passes only the height")

        ' Recommend for this PC only uses a session with the same field of view; older sessions had the full view.
        Dim last As New LastVrSession With {.VrQuality = New Dictionary(Of String, String)(GraphicsRecommendation.VrQuality(Settings())), .Performance = New MeasuredPerformance With {
            .TargetFps = 90, .FpsMedian = 72, .FpsLow = 61, .Windows = 240, .GpuPercent = 97, .Limit = "gpu", .HeadsetWidth = 2400, .HeadsetHeight = 2600, .Scale = 100}}
        check(last.VrFov = "100x100" AndAlso GraphicsRecommendation.Recommend(last, Settings(), 2400, 2600).Preset = GraphicsRecommendation.Keep, "the same field of view: a recommendation")
        check(GraphicsRecommendation.Recommend(last, Settings(90), 2400, 2600).Reason.Contains("other VR quality"), "another field of view: none")
        Dim folder = Path.Combine(ctx.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(folder)
        File.WriteAllText(Path.Combine(folder, "native-1.log"), "XR prototype enabled headset=2400x2600 render_scale=100 scene=2160x2340 game_units_per_metre=1" & vbLf & "XR prototype main=1 submitted=1")
        SafeFiles.SaveJson(Path.Combine(folder, "session.json"), New With {.schema = 1, .mode = "experimental-interactive", .vrFovWidth = 100, .vrFovHeight = 85})
        check(LastVrSession.Load(ctx).VrFov = "100x85", "the session records its field of view")
        SafeFiles.SaveJson(Path.Combine(folder, "session.json"), New With {.schema = 1, .mode = "experimental-interactive"})
        check(LastVrSession.Load(ctx).VrFov = "100x100", "an older session counts as the full view")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call Settings().Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim slider = DirectCast(form.Controls.Find("VrFieldOfView", True).Single(), ValueSlider)
            Dim helmet = DirectCast(form.Controls.Find("VrHelmetView", True).Single(), CheckBox)
            check(slider.Value = 100 AndAlso Not helmet.Checked AndAlso Not helmet.Enabled AndAlso Not form.HasUnsavedSettings(), "Field of view starts at 100%; helmet view waits for a crop")
            slider.Value = 85 : Application.DoEvents()
            helmet.Checked = True : Application.DoEvents()
            check(helmet.Enabled AndAlso form.CurrentSettings().VrFieldOfView = 85 AndAlso form.CurrentSettings().VrHelmetView AndAlso form.HasUnsavedSettings(), "85% with helmet view are launcher settings")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.VrFieldOfView = 85 AndAlso saved.VrHelmetView, "Save settings keeps them")
            form.Close()
        End Using
    End Sub
End Module

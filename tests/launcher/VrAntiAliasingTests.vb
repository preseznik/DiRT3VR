Imports DiRT3VR
Imports System.IO
Imports System.Text
Imports System.Windows.Forms
Imports System.Xml.Linq

' VR anti-aliasing (#c8076a1e, launcher side; Lens's runs: the eye copy works unchanged at 2xmsaa and 4xmsaa):
' Graphics → VR rendering → Anti-aliasing writes DiRT 3's multisampling into the VR session's temporary graphics
' file, recovery recomputes that exact transform, and Recommend for this PC compares it.
Module VrAntiAliasingTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "vr-msaa", name)
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "vr-msaa-users", name), Path.Combine(game, "graphics.xml"), Path.Combine(game, "package"))
        Directory.CreateDirectory(Path.Combine(game, "system")) : Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"), True)
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31", "original.xml"), ctx.GraphicsPath, True)
        Return ctx
    End Function

    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Multisampling(data As Byte()) As String
        Return XDocument.Parse(Encoding.UTF8.GetString(data)).Root.Element("graphics_card").Element("resolution").Attribute("multisampling").Value
    End Function

    Private Function Settings(Optional antiAliasing As String = "off") As LauncherSettings
        Return New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .VrAntiAliasing = antiAliasing}
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        ' The setting: off by default, only the tested values.
        check(Settings().VrAntiAliasing = "off" AndAlso LauncherSettings.VrAntiAliasingChoices.SequenceEqual({"off", "2xmsaa", "4xmsaa"}), "off by default; off, 2× and 4×")
        Settings("4xmsaa").Validate()
        check(Refused(Sub() Settings("8xmsaa").Validate()) AndAlso Refused(Sub() Settings("on").Validate()), "8× (untested) and other values are refused")
        check(ProfileValues.Capture(Settings()).ApplyTo(Settings("2xmsaa")).VrAntiAliasing = "2xmsaa" AndAlso LauncherDefaults.Graphics(Settings("4xmsaa")).VrAntiAliasing = "off",
              "a settings profile keeps this PC's choice; Restore graphics defaults turns it off")

        ' The VR session's graphics file, and recovery of exactly that transform.
        Dim ctx = Context(root, "session")
        Dim original = File.ReadAllBytes(ctx.GraphicsPath)
        check(Multisampling(ExperimentalFiles.Changes(ctx, 1800, 1950)("graphics")) = "off", "a VR session still forces it off by default")
        Dim changes = ExperimentalFiles.Changes(ctx, 1800, 1950, multisampling:="4xmsaa").ToDictionary(Function(pair) pair.Key, Function(pair) pair.Value)
        check(Multisampling(changes("graphics")) = "4xmsaa" AndAlso ExperimentalFiles.AppliedMultisampling(changes("graphics")) = "4xmsaa", "4× is written into the session's graphics file and read back")
        check(Refused(Sub() ExperimentalFiles.Graphics(original, 1800, 1950, multisampling:="8xmsaa")) AndAlso Refused(Sub() ExperimentalFiles.AppliedMultisampling(original.Take(0).ToArray())), "other values are refused")
        Dim transaction As New OwnedFileTransaction(ctx)
        transaction.Prepare(changes, experimental:=True, apply:=False)
        File.WriteAllBytes(ctx.GraphicsPath, changes("graphics")) : File.WriteAllBytes(Path.Combine(ctx.GameRoot, "d3d11.dll"), changes("proxy"))
        File.WriteAllBytes(Path.Combine(ctx.GameRoot, "system", "hardware_settings_restrictions.xml"), changes("restrictions"))
        check(transaction.Recover() AndAlso File.ReadAllBytes(ctx.GraphicsPath).SequenceEqual(original), "after a 4× session the graphics file is exactly the original again")
        Dim tampered = XDocument.Parse(Encoding.UTF8.GetString(changes("graphics")))
        tampered.Root.Element("graphics_card").Element("resolution").SetAttributeValue("multisampling", "8xmsaa")
        Dim other As New OwnedFileTransaction(Context(root, "tampered"))
        check(Refused(Sub() other.Prepare(New Dictionary(Of String, Byte()) From {{"graphics", Encoding.UTF8.GetBytes(tampered.ToString(SaveOptions.DisableFormatting))}, {"proxy", changes("proxy")}, {"restrictions", changes("restrictions")}}, experimental:=True, apply:=False)),
              "a prepared file with an unsupported anti-aliasing isn't an approved transform")

        ' Recommend for this PC only uses a session with the same anti-aliasing; older sessions ran with it off.
        Dim last As New LastVrSession With {.VrQuality = New Dictionary(Of String, String)(GraphicsRecommendation.VrQuality(Settings())), .Performance = New MeasuredPerformance With {
            .TargetFps = 90, .FpsMedian = 72, .FpsLow = 61, .Windows = 240, .GpuPercent = 97, .Limit = "gpu", .HeadsetWidth = 2400, .HeadsetHeight = 2600, .Scale = 100}}
        check(last.VrAntiAliasing = "off" AndAlso GraphicsRecommendation.Recommend(last, Settings(), 2400, 2600).Preset = GraphicsRecommendation.Keep, "the same anti-aliasing (off, as older sessions): a recommendation")
        check(GraphicsRecommendation.Recommend(last, Settings("2xmsaa"), 2400, 2600).Reason.Contains("other VR quality"), "another anti-aliasing: no recommendation")
        Dim folder = Path.Combine(ctx.UserRoot, "logs", "session-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(folder)
        File.WriteAllText(Path.Combine(folder, "native-1.log"), "XR prototype enabled headset=2400x2600 render_scale=100 scene=2400x2600 game_units_per_metre=1" & vbLf & "XR prototype main=1 submitted=1")
        SafeFiles.SaveJson(Path.Combine(folder, "session.json"), New With {.schema = 1, .mode = "experimental-interactive", .vrAntiAliasing = "4xmsaa"})
        check(LastVrSession.Load(ctx).VrAntiAliasing = "4xmsaa", "the session records its anti-aliasing")
        SafeFiles.SaveJson(Path.Combine(folder, "session.json"), New With {.schema = 1, .mode = "experimental-interactive"})
        check(LastVrSession.Load(ctx).VrAntiAliasing = "off", "an older session counts as off")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "ui")
        Call Settings().Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim box = DirectCast(form.Controls.Find("VrAntiAliasing", True).Single(), ComboBox)
            check(box.Items.Count = 3 AndAlso box.SelectedIndex = 0 AndAlso Not form.HasUnsavedSettings(), "Graphics → VR rendering → Anti-aliasing starts on Off")
            box.SelectedIndex = 2 : Application.DoEvents()
            check(form.CurrentSettings().VrAntiAliasing = "4xmsaa" AndAlso form.HasUnsavedSettings(), "4× is a launcher setting")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            check(LauncherSettings.Load(ctx).VrAntiAliasing = "4xmsaa", "Save settings keeps it")
            form.Close()
        End Using
    End Sub
End Module

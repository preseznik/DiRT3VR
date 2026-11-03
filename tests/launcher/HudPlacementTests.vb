Imports DiRT3VR
Imports System.IO
Imports System.Windows.Forms

' The VR HUD panel's distance and follow mode (#3652b9aa): the launcher setting and the environment the
' renderer reads (Lens's contract: DIRT3VR_HUD_DISTANCE metres in invariant culture, DIRT3VR_HUD_FOLLOW 1 or 0).
Module HudPlacementTests
    Private Function Refused(action As Action) As Boolean
        Try
            action()
        Catch ex As IOException
            Return True
        End Try
        Return False
    End Function

    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, name)
        Directory.CreateDirectory(game)
        Return New InstallContext(game, Path.Combine(root, name & "-users"), Path.Combine(game, "graphics.xml"), root)
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim defaults As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}
        check(defaults.HudDistance = 2D AndAlso Not defaults.HudFollowView, "the HUD stays 2 m away where you recentered, by default")
        For Each bad In {0.5D, 5.5D, 2.25D, -1D}
            Dim value = bad
            check(Refused(Sub() Call New LauncherSettings With {.HudDistance = value}.Validate()), "a HUD distance of " & value & " m is refused")
        Next
        check(Not Refused(Sub() Call New LauncherSettings With {.HudDistance = 1D}.Validate()) AndAlso Not Refused(Sub() Call New LauncherSettings With {.HudDistance = 4.5D}.Validate()) AndAlso
              Not Refused(Sub() Call New LauncherSettings With {.HudDistance = 5D}.Validate()), "1 to 5 m in half-metre steps are kept")

        Dim ctx = Context(root, "hud-placement")
        Dim headset As New HeadsetResolution("vdxr", "Test headset", 2400, 2600)
        Dim runtime As New RuntimeInfo With {.Mode = "vdxr", .ManifestPath = "C:\runtime\virtualdesktop-openxr-32.json"}
        Dim logs = Path.Combine(ctx.UserRoot, "logs", "fixture")
        Dim plain = InteractiveSession.StartInfo(ctx, runtime, logs, defaults, headset)
        check(plain.Environment("DIRT3VR_HUD_DISTANCE") = "2.0" AndAlso plain.Environment("DIRT3VR_HUD_FOLLOW") = "0", "a VR session passes the defaults (2.0, 0)")
        Dim culture = Globalization.CultureInfo.CurrentCulture
        Try
            Globalization.CultureInfo.CurrentCulture = New Globalization.CultureInfo("de-DE")
            Dim moved = InteractiveSession.StartInfo(ctx, runtime, logs, New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .HudDistance = 3.5D, .HudFollowView = True}, headset)
            check(moved.Environment("DIRT3VR_HUD_DISTANCE") = "3.5" AndAlso moved.Environment("DIRT3VR_HUD_FOLLOW") = "1", "3.5 m and follow view are passed with a decimal point, whatever the Windows language")
        Finally
            Globalization.CultureInfo.CurrentCulture = culture
        End Try

        ' Saved with the launcher settings; settings profiles keep this PC's choice, like recentering.
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .HudDistance = 1.5D, .HudFollowView = True}.Save(ctx)
        Dim loaded = LauncherSettings.Load(ctx)
        check(loaded.HudDistance = 1.5D AndAlso loaded.HudFollowView, "the HUD placement round trips through launcher settings")
        Dim applied = ProfileValues.Capture(defaults).ApplyTo(loaded)
        check(applied.HudDistance = 1.5D AndAlso applied.HudFollowView, "loading a settings profile keeps this PC's HUD placement")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim ctx = Context(root, "hud-placement-ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim distance = DirectCast(form.Controls.Find("HudDistance", True).Single(), ValueSlider)
            Dim follow = DirectCast(form.Controls.Find("HudFollowView", True).Single(), CheckBox)
            check(form.SectionHeaders.Any(Function(header) header.Title = "VR HUD") AndAlso distance.Value = 4 AndAlso Not follow.Checked AndAlso Not form.HasUnsavedSettings(),
                  "Graphics → VR HUD shows 2 m, not following the view, by default")
            distance.Value = 7 : follow.Checked = True : Application.DoEvents()
            check(form.CurrentSettings().HudDistance = 3.5D AndAlso form.CurrentSettings().HudFollowView AndAlso form.HasUnsavedSettings(), "3.5 m and follow view are launcher settings")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            Dim saved = LauncherSettings.Load(ctx)
            check(saved.HudDistance = 3.5D AndAlso saved.HudFollowView AndAlso Not form.HasUnsavedSettings(), "Save settings keeps them")
            form.Close()
        End Using
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            check(DirectCast(form.Controls.Find("HudDistance", True).Single(), ValueSlider).Value = 7 AndAlso DirectCast(form.Controls.Find("HudFollowView", True).Single(), CheckBox).Checked,
                  "the saved placement shows again")
            form.Close()
        End Using
    End Sub
End Module

Imports DiRT3VR
Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Windows.Forms
Imports System.Xml.Linq

Module DesktopTests
    Private ReadOnly Fixture As Byte() = Encoding.UTF8.GetBytes("<?xml version='1.0'?><!-- keep --><hardware_settings_config version='56'><cpu><threadStrategy parallelUpdateRender='true'/></cpu><graphics_card><directx forcedx9='false'/><stereo enabled='false'/><resolution width='822' height='656' aspect='auto' fullscreen='false' vsync='0' multisampling='4xmsaa'><refreshRate rate='120'/></resolution><gamma level='1.0'/></graphics_card><audio mixing='original'/></hardware_settings_config>")

    Private Function ReadResolution(bytes As Byte()) As XElement
        Return XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root.Element("graphics_card").Element("resolution")
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim folder = Path.Combine(root, "desktop")
        Directory.CreateDirectory(folder)
        Dim context As New InstallContext(folder, Path.Combine(root, "users"), Path.Combine(folder, "graphics.xml"), root)
        Dim settings = LauncherSettings.Load(context)
        check(Not DesktopGraphics.HasOverrides(settings), "desktop defaults preserve game settings")
        DesktopGraphics.Apply(context, settings, Size.Empty)
        check(Not File.Exists(context.GraphicsPath), "default first launch creates no graphics file")
        check(Fixture.SequenceEqual(DesktopGraphics.Transform(Fixture, settings, Size.Empty)), "default desktop transform preserves exact bytes")
        File.WriteAllBytes(context.GraphicsPath, Fixture)
        settings.DesktopWidth = 1920 : settings.DesktopHeight = 1080
        settings.DesktopDisplayMode = "fullscreen" : settings.DesktopVSync = "on"
        settings.Save(context)
        Dim saved = LauncherSettings.Load(context)
        check(saved.DesktopWidth = 1920 AndAlso saved.DesktopHeight = 1080 AndAlso saved.DesktopDisplayMode = "fullscreen" AndAlso saved.DesktopVSync = "on", "desktop settings round trip")
        check(File.ReadAllBytes(context.GraphicsPath).SequenceEqual(Fixture), "Save settings never writes game graphics")
        DesktopGraphics.Apply(context, saved, Size.Empty)
        Dim applied = File.ReadAllBytes(context.GraphicsPath)
        Dim resolution = ReadResolution(applied)
        check(resolution.Attribute("width").Value = "1920" AndAlso resolution.Attribute("height").Value = "1080" AndAlso resolution.Attribute("fullscreen").Value = "true" AndAlso resolution.Attribute("vsync").Value = "1", "desktop choices reach the real XML fields")
        Dim expected = XDocument.Parse(Encoding.UTF8.GetString(Fixture))
        Dim expectedResolution = expected.Root.Element("graphics_card").Element("resolution")
        For Each name In {"width", "height", "fullscreen", "vsync"}
            expectedResolution.SetAttributeValue(name, resolution.Attribute(name).Value)
        Next
        check(ExperimentalFiles.SameXml(Encoding.UTF8.GetBytes(expected.ToString()), applied), "all unrelated XML, including comments, MSAA, aspect and refresh, survives")
        Dim backup = Path.Combine(context.UserRoot, "desktop-backups", SafeFiles.HashBytes(Fixture) & ".xml")
        check(File.ReadAllBytes(backup).SequenceEqual(Fixture), "desktop change has an exact original backup")
        DesktopGraphics.Apply(context, saved, Size.Empty)
        check(Directory.GetFiles(Path.GetDirectoryName(backup)).Length = 1 AndAlso File.ReadAllBytes(context.GraphicsPath).SequenceEqual(applied), "reapplying matching choices makes no write or extra backup")
        ' A read-only graphics file (players use this to stop the game resetting it) is changed and stays read-only.
        SafeFiles.SetReadOnly(context.GraphicsPath, True)
        Dim lockedChoice As New LauncherSettings With {.DesktopWidth = 1920, .DesktopHeight = 1080, .DesktopDisplayMode = "windowed", .DesktopVSync = "off"}
        DesktopGraphics.Apply(context, lockedChoice, Size.Empty)
        check(ReadResolution(File.ReadAllBytes(context.GraphicsPath)).Attribute("fullscreen").Value = "false" AndAlso SafeFiles.IsReadOnly(context.GraphicsPath), "desktop choices apply to a read-only graphics file and keep it read-only")
        SafeFiles.SetReadOnly(context.GraphicsPath, False)
        DesktopGraphics.Apply(context, saved, Size.Empty)
        check(ExperimentalFiles.SameXml(File.ReadAllBytes(context.GraphicsPath), applied) AndAlso Not SafeFiles.IsReadOnly(context.GraphicsPath), "desktop choices reapply after the read-only check")
        For Each mode In {"game", "windowed", "fullscreen", "borderless"}
            For Each vsync In {"game", "off", "on"}
                Dim choice As New LauncherSettings With {.DesktopDisplayMode = mode, .DesktopVSync = vsync}
                Dim output = DesktopGraphics.Transform(Fixture, choice, New Size(2560, 1440))
                Dim values = ReadResolution(output)
                check(values.Attribute("fullscreen").Value = If(mode = "fullscreen", "true", "false"), "desktop mode mapping: " & mode)
                check(values.Attribute("vsync").Value = If(vsync = "on", "1", "0"), "VSync mode mapping: " & vsync)
                check(values.Attribute("width").Value = If(mode = "borderless", "2560", "822"), "borderless alone uses main-display dimensions")
            Next
        Next
        Dim borderless As New LauncherSettings With {.DesktopDisplayMode = "borderless", .DesktopWidth = 1280, .DesktopHeight = 720, .DesktopVSync = "on"}
        Dim borderlessBytes = DesktopGraphics.Transform(Fixture, borderless, New Size(2560, 1440))
        check(ReadResolution(borderlessBytes).Attribute("width").Value = "2560" AndAlso borderless.DesktopWidth = 1280, "borderless ignores but preserves the chosen windowed resolution")
        Dim vr = ReadResolution(ExperimentalFiles.Graphics(borderlessBytes, 3072, 3264))
        check(vr.Attribute("width").Value = "3072" AndAlso vr.Attribute("height").Value = "3264" AndAlso vr.Attribute("vsync").Value = "0" AndAlso vr.Attribute("fullscreen").Value = "false", "VR profile overrides desktop choices independently")
        For Each invalid In {New LauncherSettings With {.DesktopWidth = 1920}, New LauncherSettings With {.DesktopWidth = -1, .DesktopHeight = -1}, New LauncherSettings With {.DesktopWidth = 9000, .DesktopHeight = 600},
                             New LauncherSettings With {.DesktopDisplayMode = Nothing}, New LauncherSettings With {.DesktopDisplayMode = "invalid"}, New LauncherSettings With {.DesktopVSync = "true"}, New LauncherSettings With {.DesktopVSync = Nothing}}
            Refuse(Sub() invalid.Validate(), check, "invalid desktop preference rejected")
        Next
        Refuse(Sub() DesktopGraphics.Transform(Fixture, borderless, Size.Empty), check, "borderless refuses a missing display")
        For Each invalid In {"<broken", "<wrong/>", "<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///none'>]><a>&x;</a>", Encoding.UTF8.GetString(Fixture).Replace("width='822'", ""), Encoding.UTF8.GetString(Fixture).Replace("</graphics_card>", "<resolution/></graphics_card>")}
            File.WriteAllText(context.GraphicsPath, invalid)
            Refuse(Sub() DesktopGraphics.Apply(context, saved, Size.Empty), check, "malformed or ambiguous graphics rejected")
            check(File.ReadAllText(context.GraphicsPath) = invalid, "rejected graphics preserved byte for byte")
        Next
        File.WriteAllBytes(context.GraphicsPath, Fixture)
        File.WriteAllText(backup, "modified backup")
        Refuse(Sub() DesktopGraphics.Apply(context, saved, Size.Empty), check, "modified backup is not overwritten")
        check(File.ReadAllBytes(context.GraphicsPath).SequenceEqual(Fixture) AndAlso File.ReadAllText(backup) = "modified backup", "backup conflict preserves both files")
        SafeFiles.SaveJson(New OwnedFileTransaction(context).JournalPath, New With {.pending = True})
        Refuse(Sub() DesktopGraphics.Apply(context, saved, Size.Empty), check, "pending recovery blocks desktop changes")
        check(File.ReadAllBytes(context.GraphicsPath).SequenceEqual(Fixture), "pending recovery protects graphics")
        Dim legacy As New InstallContext(Path.Combine(root, "desktop-legacy"), Path.Combine(root, "users"))
        SafeFiles.SaveJson(legacy.PreferencesPath, New With {.Version = 1, .VrRenderScale = 150})
        check(Not DesktopGraphics.HasOverrides(LauncherSettings.Load(legacy)) AndAlso LauncherSettings.Load(legacy).VrRenderScale = 150, "legacy preferences keep native desktop defaults and VR scale")
    End Sub

    Public Sub RunWindows(check As Action(Of Boolean, String))
        Dim sizes = DesktopDisplay.Resolutions()
        check(sizes.Length > 0 AndAlso sizes.Distinct().Count() = sizes.Length, "Windows display modes produce unique desktop resolutions")
        For Each size In sizes
            DesktopGraphics.ValidateSize(size)
        Next
        For Each fail As Boolean In {False, True}
            Using window As New Form With {.StartPosition = FormStartPosition.Manual, .Bounds = New Rectangle(-30000, -30000, 900, 700), .ShowInTaskbar = False, .Opacity = 0, .TopMost = fail}
                window.Show() : Application.DoEvents()
                Dim original = BorderlessWindow.ReadBounds(window.Handle), originalStyle = BorderlessWindow.ReadStyle(window.Handle, -16)
                Dim originalExStyle = BorderlessWindow.ReadStyle(window.Handle, -20)
                Dim target As New Rectangle(-28000, -28000, 1280, 720)
                Dim wrongOwner As New BorderlessWindow(Environment.ProcessId + 1, target)
                check(Not wrongOwner.UpdateWindow(window.Handle, 0) AndAlso BorderlessWindow.ReadBounds(window.Handle) = original, "borderless refuses another process's window")
                Dim placement As New BorderlessWindow(Environment.ProcessId, target)
                check(Not placement.UpdateWindow(window.Handle, 0), "borderless waits for placement confirmation")
                Application.DoEvents()
                If fail Then
                    window.Bounds = original
                    Application.DoEvents()
                End If
                check(placement.UpdateWindow(window.Handle, 6000), "placement completes or rolls back within five seconds")
                Application.DoEvents()
                If fail Then
                    check(placement.Warning <> "" AndAlso BorderlessWindow.ReadBounds(window.Handle) = original AndAlso BorderlessWindow.ReadStyle(window.Handle, -16) = originalStyle AndAlso BorderlessWindow.ReadStyle(window.Handle, -20) = originalExStyle, "rejected placement restores original frame, topmost state and bounds")
                Else
                    check(placement.Warning = "" AndAlso BorderlessWindow.ReadBounds(window.Handle) = target AndAlso (BorderlessWindow.ReadStyle(window.Handle, -16) And BorderlessWindow.FrameBits) = 0, "owned window becomes borderless at requested bounds")
                    window.Bounds = original
                    placement.UpdateWindow(window.Handle, 7000)
                    check(BorderlessWindow.ReadBounds(window.Handle) = original, "borderless does not fight later user window changes")
                End If
                Dim interrupted As New BorderlessWindow(Environment.ProcessId, target)
                Dim before = BorderlessWindow.ReadBounds(window.Handle), beforeStyle = BorderlessWindow.ReadStyle(window.Handle, -16)
                interrupted.UpdateWindow(window.Handle, 0)
                Application.DoEvents()
                interrupted.Cancel()
                Application.DoEvents()
                check(BorderlessWindow.ReadBounds(window.Handle) = before AndAlso BorderlessWindow.ReadStyle(window.Handle, -16) = beforeStyle, "closing launcher mid-placement restores the previous window")
            End Using
        Next
    End Sub

    Private Sub Refuse(action As Action, check As Action(Of Boolean, String), message As String)
        Try
            action()
        Catch ex As IOException
            check(True, message) : Return
        End Try
        check(False, message)
    End Sub
End Module

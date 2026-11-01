Imports DiRT3VR
Imports System.IO
Imports System.Text
Imports System.Windows.Forms
Imports System.Xml.Linq

Module GraphicsQualityTests
    Private Function Context(root As String, name As String) As InstallContext
        Dim game = Path.Combine(root, "graphics-quality", name)
        Directory.CreateDirectory(game)
        Dim ctx As New InstallContext(game, Path.Combine(root, "graphics-quality-users", name), Path.Combine(game, "graphics.xml"), Path.Combine(game, "package"))
        ' What a VR session needs besides the graphics file.
        Directory.CreateDirectory(Path.Combine(game, "system")) : Directory.CreateDirectory(Path.Combine(ctx.PackageRoot, "payload"))
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "hardware_settings_restrictions.xml"), Path.Combine(game, "system", "hardware_settings_restrictions.xml"), True)
        File.WriteAllText(Path.Combine(ctx.PackageRoot, "payload", "d3d11.dll"), "owned test payload")
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

    ' A real hardware_settings_config.xml (31 Oct, Ultra values with a "medium" label) with every quality element.
    Private Function RealFile() As Byte()
        Return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "rewrite-2026-10-31", "original.xml"))
    End Function

    Private Function Attr(data As Byte(), element As String, name As String) As String
        Return XDocument.Parse(Encoding.UTF8.GetString(data)).Root.Element(element)?.Attribute(name)?.Value
    End Function

    Public Sub Run(root As String, check As Action(Of Boolean, String))
        Dim catalog = GraphicsCatalog.Current
        check(catalog.Categories.Count = 15 AndAlso catalog.Presets.Select(Function(preset) preset.Id).SequenceEqual({"ultralow", "low", "medium", "high", "ultra"}), "the embedded catalog has DiRT 3's 15 quality options and 5 presets")
        Dim medium = catalog.Presets.Single(Function(preset) preset.Id = "medium")
        check(catalog.Categories.All(Function(category) medium.Values(category.Field) = category.Default), "every option's default is the game's default (Medium) preset")
        check(catalog.MatchingPreset(New Dictionary(Of String, String)(catalog.Presets(3).Values)) Is catalog.Presets(3), "a full preset is recognized")
        Dim custom As New Dictionary(Of String, String)(catalog.Presets(3).Values)
        custom("Shadows") = "ultra"
        check(catalog.MatchingPreset(custom) Is Nothing AndAlso catalog.MatchingPreset(New Dictionary(Of String, String)) Is Nothing, "changed or empty choices are Custom / Use game settings")
        check(Refused(Sub() catalog.Normalize(New Dictionary(Of String, String) From {{"Bloom", "off"}})) AndAlso Refused(Sub() catalog.Normalize(New Dictionary(Of String, String) From {{"Shadows", "extreme"}})), "unknown options and values are refused")
        check(catalog.Normalize(New Dictionary(Of String, String) From {{"Water", "high"}, {"Shadows", "game"}, {"Crowd", "off"}}).Keys.SequenceEqual({"Crowd", "Water"}), "choices are kept in catalog order without Use game setting entries")

        ' The transform writes exactly the attributes DiRT 3's options write and nothing else.
        Dim real = RealFile()
        Dim none As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}
        check(DesktopGraphics.Transform(real, none, Drawing.Size.Empty).SequenceEqual(real), "no quality choices leave the file byte for byte")
        Dim low As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .GraphicsQuality = New Dictionary(Of String, String)(catalog.Presets(1).Values)}
        Dim lowFile = DesktopGraphics.Transform(real, low, Drawing.Size.Empty)
        Dim everyAttribute = catalog.Categories.All(Function(category)
                                                        Dim choice = category.Find(low.GraphicsQuality(category.Field))
                                                        Return choice.Attributes.All(Function(pair) Attr(lowFile, pair.Key.Split("@"c)(0), pair.Key.Split("@"c)(1)) = pair.Value)
                                                    End Function)
        check(everyAttribute, "the Low preset writes every attribute of every option")
        check(Attr(lowFile, "graphics_detail", "level") = "medium" AndAlso Attr(lowFile, "physics", "vehicleDamage") = "true" AndAlso Attr(lowFile, "motion", "ip") = "dbox" AndAlso
              XDocument.Parse(Encoding.UTF8.GetString(lowFile)).Root.Element("graphics_card").Element("resolution").Attribute("width").Value = "1920", "display, damage, motion and the preset label stay as they were")
        Dim crowdOff As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .GraphicsQuality = New Dictionary(Of String, String) From {{"Crowd", "off"}}}
        Dim crowdFile = DesktopGraphics.Transform(real, crowdOff, Drawing.Size.Empty)
        check(Attr(crowdFile, "crowd", "enabled") = "false" AndAlso Attr(crowdFile, "crowd", "detail") = Attr(real, "crowd", "detail") AndAlso Attr(crowdFile, "shadows", "size") = Attr(real, "shadows", "size"), "Crowd Off writes only enabled, like the game; other options are untouched")
        Dim ultra As New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .GraphicsQuality = New Dictionary(Of String, String)(catalog.Presets(4).Values)}
        check(DesktopGraphics.Transform(real, ultra, Drawing.Size.Empty).SequenceEqual(real), "values that already match (this file is Ultra) leave the file byte for byte")
        Dim missing = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(real).Replace("<water update=""true"" detail=""2"" tessellation=""true"" />", ""))
        check(Not missing.SequenceEqual(real) AndAlso Refused(Sub() DesktopGraphics.Transform(missing, low, Drawing.Size.Empty)), "a file without one of the options is refused and preserved")

        ' Applied with a backup, for desktop launches and (quality only) before VR sessions.
        Dim ctx = Context(root, "apply")
        File.WriteAllBytes(ctx.GraphicsPath, real)
        DesktopGraphics.Apply(ctx, DesktopGraphics.QualityOnly(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .DesktopDisplayMode = "windowed", .GraphicsQuality = New Dictionary(Of String, String)(catalog.Presets(1).Values)}), Drawing.Size.Empty)
        Dim applied = File.ReadAllBytes(ctx.GraphicsPath)
        check(Attr(applied, "shadows", "size") = "768" AndAlso XDocument.Parse(Encoding.UTF8.GetString(applied)).Root.Element("graphics_card").Element("resolution").Attribute("fullscreen").Value = "true",
              "a VR launch applies the quality choices but not the desktop display choices")
        check(File.Exists(Path.Combine(ctx.UserRoot, "desktop-backups", SafeFiles.HashBytes(real) & ".xml")), "the original graphics file is backed up before the change")
        Dim changes = ExperimentalFiles.Changes(ctx, 1800, 1950)
        check(Attr(changes("graphics"), "shadows", "size") = "768", "the VR session's temporary file starts from the quality-applied file, so recovery returns to it")

        ' Launcher settings and settings profiles (schema 4).
        Dim saved = Context(root, "settings")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = "", .GraphicsQuality = New Dictionary(Of String, String) From {{"Water", "high"}, {"Trees", "ultra"}}}.Save(saved)
        Dim loaded = LauncherSettings.Load(saved)
        check(loaded.GraphicsQuality.Keys.SequenceEqual({"Trees", "Water"}) AndAlso loaded.GraphicsQuality("Water") = "high", "quality choices are saved with the launcher settings")
        SafeFiles.SaveJson(saved.PreferencesPath, New With {.Version = 1, .GraphicsQuality = New With {.Shadows = "extreme"}})
        check(Refused(Sub() LauncherSettings.Load(saved)), "an edited, invalid quality choice is refused")
        Dim profile = ProfileValues.Capture(loaded)
        check(profile.GraphicsQuality("Trees") = "ultra" AndAlso profile.ApplyTo(New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}).GraphicsQuality("Water") = "high", "settings profiles carry the quality choices")
        check(Refused(Sub() Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = 3, .Name = "Old", .Settings = profile}.Validate()), "a schema 3 profile can't carry quality choices")
        Call New SettingsProfile With {.Kind = "DiRT3VR.SettingsProfile", .Schema = SettingsProfile.CurrentSchema, .Name = "New", .Settings = profile}.Validate()
        check(LauncherDefaults.Graphics(loaded).GraphicsQuality.Count = 0 AndAlso LauncherDefaults.All(loaded).GraphicsQuality.Count = 0, "both restore-defaults buttons set every quality option to Use game setting")
    End Sub

    Public Sub RunUi(root As String, check As Action(Of Boolean, String))
        Dim catalog = GraphicsCatalog.Current
        Dim ctx = Context(root, "ui")
        Call New LauncherSettings With {.SteamRuntimeManifest = "", .VdxrRuntimeManifest = ""}.Save(ctx)
        Using form As New MainForm(ctx)
            form.ShowInTaskbar = False : form.StartPosition = FormStartPosition.Manual : form.Location = New Drawing.Point(-30000, -30000)
            form.Show() : Application.DoEvents()
            Dim preset = DirectCast(form.Controls.Find("QualityPreset", True).Single(), ComboBox)
            Dim box = Function(field As String) DirectCast(form.Controls.Find("Quality" & field, True).Single(), ComboBox)
            check(preset.SelectedIndex = 0 AndAlso catalog.Categories.All(Function(category) box(category.Field).SelectedIndex = 0) AndAlso form.CurrentSettings().GraphicsQuality.Count = 0,
                  "every quality option starts on Use game setting")
            check(preset.Items.Count = catalog.Presets.Count + 2 AndAlso preset.Items(3).ToString().Contains("game default"), "the preset list has Use game settings, the five presets and Custom")
            preset.SelectedIndex = 4 ' High
            Dim high = catalog.Presets(3)
            check(form.CurrentSettings().GraphicsQuality.Count = 15 AndAlso high.Values.All(Function(pair) form.CurrentSettings().GraphicsQuality(pair.Key) = pair.Value) AndAlso form.HasUnsavedSettings(), "choosing High sets every option to High's values")
            box("Shadows").SelectedIndex = box("Shadows").Items.Count - 1 ' Ultra
            check(preset.SelectedIndex = catalog.Presets.Count + 1 AndAlso form.CurrentSettings().GraphicsQuality("Shadows") = "ultra", "changing one option shows Custom")
            box("Shadows").SelectedIndex = catalog.Categories.Single(Function(category) category.Field = "Shadows").Choices.FindIndex(Function(choice) choice.Id = "high") + 1
            check(preset.SelectedIndex = 4, "putting it back shows High again")
            DirectCast(form.Controls.Find("SaveSettings", True).Single(), Button).PerformClick()
            check(LauncherSettings.Load(ctx).GraphicsQuality.Count = 15, "Save settings keeps the quality choices")
            form.ConfirmPrompt = Function(caption, body) True
            form.RestoreDefaults(graphicsOnly:=True)
            check(preset.SelectedIndex = 0 AndAlso form.CurrentSettings().GraphicsQuality.Count = 0, "Restore graphics defaults puts every option back on Use game setting")
            form.Close()
        End Using
    End Sub
End Module

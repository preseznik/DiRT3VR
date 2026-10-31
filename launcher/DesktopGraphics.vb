Imports System.Drawing
Imports System.Text
Imports System.Xml
Imports System.Xml.Linq

' Persistent desktop preferences; VR continues to use ExperimentalFiles and its own recovery transaction.
Public Module DesktopGraphics
    Public Function HasOverrides(settings As LauncherSettings) As Boolean
        Return settings.DesktopWidth <> 0 OrElse settings.DesktopDisplayMode <> "game" OrElse settings.DesktopVSync <> "game"
    End Function

    Public Sub Validate(settings As LauncherSettings)
        If Not {"game", "windowed", "fullscreen", "borderless"}.Contains(settings.DesktopDisplayMode) Then Throw New IOException("Choose a listed desktop display mode.")
        If Not {"game", "off", "on"}.Contains(settings.DesktopVSync) Then Throw New IOException("Choose Use game setting, Off or On for desktop VSync.")
        If settings.DesktopWidth = 0 AndAlso settings.DesktopHeight = 0 Then Return
        ValidateSize(New Size(settings.DesktopWidth, settings.DesktopHeight))
    End Sub

    Public Sub ValidateSize(size As Size)
        If size.Width < 640 OrElse size.Height < 480 OrElse size.Width > 8192 OrElse size.Height > 8192 Then Throw New IOException("Desktop resolution must be between 640 × 480 and 8192 × 8192.")
    End Sub

    Public Function Transform(data As Byte(), settings As LauncherSettings, monitor As Size) As Byte()
        Validate(settings)
        If Not HasOverrides(settings) Then Return data ' Exact default preservation, including a first launch without graphics XML.
        If data.Length > 1024 * 1024 Then Throw New IOException("The game graphics file is too large; it was preserved.")
        Dim document As XDocument
        Try
            Using stream As New MemoryStream(data), reader = XmlReader.Create(stream, New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Prohibit, .XmlResolver = Nothing, .MaxCharactersInDocument = 1024 * 1024})
                document = XDocument.Load(reader, LoadOptions.PreserveWhitespace)
            End Using
        Catch ex As XmlException
            Throw New IOException("The game graphics file is invalid; it was preserved.", ex)
        End Try
        If document.Root?.Name <> "hardware_settings_config" Then Throw New IOException("Unexpected game graphics file; it was preserved.")
        Dim resolutions = document.Root.Elements("graphics_card").Elements("resolution").ToArray()
        If resolutions.Length <> 1 Then Throw New IOException("Expected one game resolution entry; the file was preserved.")
        Dim resolution = resolutions(0)
        Dim changes As New Dictionary(Of String, String)
        Dim size = If(settings.DesktopDisplayMode = "borderless", monitor, New Size(settings.DesktopWidth, settings.DesktopHeight))
        If size <> Size.Empty Then
            ValidateSize(size)
            changes.Add("width", size.Width.ToString(Globalization.CultureInfo.InvariantCulture))
            changes.Add("height", size.Height.ToString(Globalization.CultureInfo.InvariantCulture))
        ElseIf settings.DesktopDisplayMode = "borderless" Then
            Throw New IOException("The main display is unavailable. Desktop settings were preserved.")
        End If
        If settings.DesktopDisplayMode <> "game" Then changes.Add("fullscreen", If(settings.DesktopDisplayMode = "fullscreen", "true", "false"))
        If settings.DesktopVSync <> "game" Then changes.Add("vsync", If(settings.DesktopVSync = "on", "1", "0"))
        For Each change In changes
            If resolution.Attribute(change.Key) Is Nothing Then Throw New IOException("Missing game graphics setting: " & change.Key & ". The file was preserved.")
            resolution.SetAttributeValue(change.Key, change.Value)
        Next
        ' Avoid reformatting and making backups when every requested value already matches.
        If ExperimentalFiles.SameXml(data, Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))) Then Return data
        Return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))
    End Function

    Public Sub Apply(context As InstallContext, settings As LauncherSettings, monitor As Size)
        Validate(settings)
        If Not HasOverrides(settings) Then Return
        context.RequireClosed()
        If New OwnedFileTransaction(context).Pending Then Throw New IOException("Restore pending session files before changing desktop graphics.")
        SafeFiles.NoLinks(context.GraphicsPath)
        If Not File.Exists(context.GraphicsPath) Then Throw New IOException("Run DiRT 3 once with desktop options set to Use game setting, then choose your desktop graphics settings.")
        If New FileInfo(context.GraphicsPath).Length > 1024 * 1024 Then Throw New IOException("The game graphics file is too large; it was preserved.")
        Dim original = File.ReadAllBytes(context.GraphicsPath), applied = Transform(original, settings, monitor)
        If original.SequenceEqual(applied) Then Return
        Dim hash = SafeFiles.HashBytes(original)
        Dim backup = Path.Combine(context.UserRoot, "desktop-backups", hash & ".xml")
        SafeFiles.NoLinks(backup)
        If File.Exists(backup) Then
            If SafeFiles.Hash(backup) <> hash Then Throw New IOException("The desktop graphics backup has changed. Both files were preserved.")
        Else
            SafeFiles.WriteAtomic(backup, original)
        End If
        If SafeFiles.Hash(context.GraphicsPath) <> hash Then Throw New IOException("The graphics file changed while preparing desktop settings. Both files were preserved.")
        SafeFiles.WriteAtomic(context.GraphicsPath, applied)
    End Sub
End Module

Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Xml
Imports System.Xml.Linq

' Only verified graphics fields and bounded scene dimensions are changed.
Public Module ExperimentalFiles
    Public Const RestrictionsHash As String = "45257db5fa64d833575d569ec6f3b03344b5f40be25830960a8d11783276470b"
    Public Const ProfileDescription As String = "Experimental VR temporarily uses DirectX 11, serial rendering, headset-relative windowed rendering, VSync off and anti-aliasing off. Original graphics settings are restored after you quit."

    Private Function ReadXml(data As Byte()) As XDocument
        If data.Length > 10 * 1024 * 1024 Then Throw New IOException("Graphics XML is too large.")
        Dim options As New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Prohibit, .XmlResolver = Nothing, .MaxCharactersInDocument = 10 * 1024 * 1024}
        Try
            Using stream As New MemoryStream(data), reader = XmlReader.Create(stream, options)
                Return XDocument.Load(reader, LoadOptions.PreserveWhitespace)
            End Using
        Catch ex As XmlException
            Throw New IOException("The graphics XML is invalid; it was preserved.", ex)
        End Try
    End Function

    Public Function Graphics(data As Byte(), Optional width As Integer = 1280, Optional height As Integer = 720) As Byte()
        HeadsetResolution.ValidateSceneSize(width, height)
        Dim document = ReadXml(data), root = document.Root
        If root Is Nothing OrElse root.Name <> "hardware_settings_config" Then Throw New IOException("Unexpected graphics XML root.")
        Dim changes As New Dictionary(Of String, Dictionary(Of String, String)) From {
            {"cpu/threadStrategy", New Dictionary(Of String, String) From {{"parallelUpdateRender", "false"}}},
            {"graphics_card/directx", New Dictionary(Of String, String) From {{"forcedx9", "false"}}},
            {"graphics_card/stereo", New Dictionary(Of String, String) From {{"enabled", "false"}}},
            {"graphics_card/resolution", New Dictionary(Of String, String) From {{"width", width.ToString(Globalization.CultureInfo.InvariantCulture)}, {"height", height.ToString(Globalization.CultureInfo.InvariantCulture)}, {"fullscreen", "false"}, {"vsync", "0"}, {"multisampling", "off"}}}}
        For Each change In changes
            Dim elements As IEnumerable(Of XElement) = {root}
            For Each name In change.Key.Split("/"c)
                elements = elements.Elements(name)
            Next
            Dim found = elements.ToArray()
            If found.Length <> 1 Then Throw New IOException("Unexpected graphics settings structure: " & change.Key)
            For Each value In change.Value
                If found(0).Attribute(value.Key) Is Nothing Then Throw New IOException("Missing graphics setting: " & value.Key)
                found(0).SetAttributeValue(value.Key, value.Value)
            Next
        Next
        Return Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))
    End Function

    Public Function Restrictions(data As Byte()) As Byte()
        If SafeFiles.HashBytes(data) <> RestrictionsHash Then Throw New IOException("Unsupported hardware restrictions file. No serial-render patch was applied.")
        Dim text = Encoding.UTF8.GetString(data)
        Dim matches = Regex.Matches(text, "<cpu\b[^>]*>.*?</cpu\s*>", RegexOptions.Singleline)
        If matches.Count <> 1 OrElse Regex.Matches(matches(0).Value, "parallelUpdateRender=""true""").Count <> 6 Then Throw New IOException("Unexpected CPU scheduling restrictions.")
        Dim block = matches(0)
        Return Encoding.UTF8.GetBytes(text.Substring(0, block.Index) & block.Value.Replace("parallelUpdateRender=""true""", "parallelUpdateRender=""false""") & text.Substring(block.Index + block.Length))
    End Function

    Private Function Canonical(node As XNode) As Object
        If TypeOf node Is XElement Then
            Dim element = DirectCast(node, XElement)
            Return New XElement(element.Name,
                element.Attributes().OrderBy(Function(a) a.Name.ToString(), StringComparer.Ordinal).Select(Function(a) New XAttribute(a)),
                element.Nodes().Select(AddressOf Canonical).Where(Function(value) value IsNot Nothing))
        End If
        If TypeOf node Is XText Then
            Dim value = DirectCast(node, XText).Value
            Return If(String.IsNullOrWhiteSpace(value), Nothing, New XText(value))
        End If
        If TypeOf node Is XComment Then Return New XComment(DirectCast(node, XComment).Value)
        If TypeOf node Is XProcessingInstruction Then
            Dim instruction = DirectCast(node, XProcessingInstruction)
            Return New XProcessingInstruction(instruction.Target, instruction.Data)
        End If
        Throw New IOException("Unrecognized graphics XML node; preserve the current file.")
    End Function

    Public Function SameXml(left As Byte(), right As Byte()) As Boolean
        Try
            Dim a = ReadXml(left), b = ReadXml(right)
            Return a.Root IsNot Nothing AndAlso b.Root IsNot Nothing AndAlso XNode.DeepEquals(
                New XDocument(a.Nodes().Select(AddressOf Canonical).Where(Function(value) value IsNot Nothing)),
                New XDocument(b.Nodes().Select(AddressOf Canonical).Where(Function(value) value IsNot Nothing)))
        Catch ex As IOException
            Return False
        End Try
    End Function

    Public Function AppliedGraphicsSize(data As Byte()) As Drawing.Size
        Dim document = ReadXml(data)
        Dim resolution = document.Root?.Element("graphics_card")?.Element("resolution")
        Dim width, height As Integer
        If resolution Is Nothing OrElse Not Integer.TryParse(resolution.Attribute("width")?.Value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, width) OrElse
            Not Integer.TryParse(resolution.Attribute("height")?.Value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, height) Then Throw New IOException("Invalid prepared scene dimensions.")
        HeadsetResolution.ValidateSceneSize(width, height)
        Return New Drawing.Size(width, height)
    End Function

    Public Function Changes(context As InstallContext, Optional width As Integer = 1280, Optional height As Integer = 720, Optional skipStartupMovies As Boolean = False) As IReadOnlyDictionary(Of String, Byte())
        Dim payload = Path.Combine(context.PackageRoot, "payload", "d3d11.dll")
        For Each target In {context.GraphicsPath, Path.Combine(context.GameRoot, "system", "hardware_settings_restrictions.xml"), payload}
            SafeFiles.NoLinks(target)
            If Not File.Exists(target) Then Throw New IOException("Required experimental VR file is missing: " & target)
        Next
        Dim preparedFiles As New Dictionary(Of String, Byte()) From {
            {"proxy", File.ReadAllBytes(payload)}, {"graphics", Graphics(File.ReadAllBytes(context.GraphicsPath), width, height)},
            {"restrictions", Restrictions(File.ReadAllBytes(Path.Combine(context.GameRoot, "system", "hardware_settings_restrictions.xml")))}}
        If skipStartupMovies Then preparedFiles.Add("startup", StartupMovies.ReadPatch(context))
        Return preparedFiles
    End Function
End Module

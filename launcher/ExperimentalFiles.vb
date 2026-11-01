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

    ' vrQuality: VR-only graphics quality choices (GraphicsCatalog), part of this temporary transform.
    Public Function Graphics(data As Byte(), Optional width As Integer = 1280, Optional height As Integer = 720, Optional vrQuality As IDictionary(Of String, String) = Nothing) As Byte()
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
        If vrQuality IsNot Nothing Then GraphicsCatalog.Current.Apply(root, vrQuality)
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

    Private Function CanonicalDocument(document As XDocument) As XDocument
        Return New XDocument(document.Nodes().Select(AddressOf Canonical).Where(Function(value) value IsNot Nothing))
    End Function

    Public Function SameXml(left As Byte(), right As Byte()) As Boolean
        Try
            Dim a = ReadXml(left), b = ReadXml(right)
            Return a.Root IsNot Nothing AndAlso b.Root IsNot Nothing AndAlso XNode.DeepEquals(CanonicalDocument(a), CanonicalDocument(b))
        Catch ex As IOException
            Return False
        End Try
    End Function

    ' The values a VR session sets (see Graphics), plus the aspect and refresh rate that DiRT 3
    ' rewrites together with the resolution when it starts.
    Private ReadOnly SessionValues As New Dictionary(Of String, String()) From {
        {"cpu/threadStrategy", New String() {"parallelUpdateRender"}},
        {"graphics_card/directx", New String() {"forcedx9"}},
        {"graphics_card/stereo", New String() {"enabled"}},
        {"graphics_card/resolution", New String() {"width", "height", "fullscreen", "vsync", "multisampling", "aspect"}}}
    Private Const RefreshRatePath As String = "graphics_card/resolution/refreshRate"

    Private Function FindOne(root As XElement, path As String) As XElement
        Dim elements As IEnumerable(Of XElement) = {root}
        For Each name In path.Split("/"c)
            elements = elements.Elements(name)
        Next
        Dim found = elements.Take(2).ToArray()
        Return If(found.Length = 1, found(0), Nothing)
    End Function

    Private Function SettingsDocument(data As Byte()) As XDocument
        Try
            Dim document = ReadXml(data)
            If document.Root Is Nothing OrElse document.Root.Name <> "hardware_settings_config" Then Return Nothing
            Return If(SessionValues.Keys.All(Function(key) FindOne(document.Root, key) IsNot Nothing), document, Nothing)
        Catch ex As IOException
            Return Nothing
        End Try
    End Function

    ' True when the current file differs from what the VR session wrote only in values the session
    ' owns. DiRT 3 does this when it starts (for example 1280x720 at 100 Hz instead of the headset
    ' size), so restoring the original settings loses nothing that was changed during the session.
    Public Function OnlySessionValuesChanged(current As Byte(), applied As Byte()) As Boolean
        Dim a = SettingsDocument(current), b = SettingsDocument(applied)
        If a Is Nothing OrElse b Is Nothing Then Return False
        For Each document In {a, b}
            For Each pair In SessionValues
                Dim element = FindOne(document.Root, pair.Key)
                For Each name In pair.Value
                    element.Attribute(name)?.Remove()
                Next
            Next
            FindOne(document.Root, "graphics_card/resolution").Elements("refreshRate").Remove()
        Next
        Return XNode.DeepEquals(CanonicalDocument(a), CanonicalDocument(b))
    End Function

    ' Keeps what changed in DiRT 3 during the session but puts back the original's values for
    ' everything the session owns. Nothing when the current file isn't DiRT 3's settings XML.
    ' vrQuality: the session's VR-only quality choices; their attributes go back to the original too.
    Public Function RestoreSessionValues(current As Byte(), original As Byte(), Optional vrQuality As IDictionary(Of String, String) = Nothing) As Byte()
        Dim target = SettingsDocument(current), source = SettingsDocument(original)
        If target Is Nothing OrElse source Is Nothing Then Return Nothing
        For Each pair In GraphicsCatalog.Current.Normalize(vrQuality)
            For Each key In GraphicsCatalog.Current.Categories.First(Function(category) category.Field = pair.Key).Find(pair.Value).Attributes.Keys
                Dim parts = key.Split("@"c)
                Dim originalElement = source.Root.Elements(parts(0)).Take(2).ToArray(), currentElement = target.Root.Elements(parts(0)).Take(2).ToArray()
                If originalElement.Length <> 1 OrElse currentElement.Length <> 1 Then Return Nothing
                Dim value = originalElement(0).Attribute(parts(1))
                If value Is Nothing Then currentElement(0).Attribute(parts(1))?.Remove() Else currentElement(0).SetAttributeValue(parts(1), value.Value)
            Next
        Next
        For Each pair In SessionValues
            Dim originalElement = FindOne(source.Root, pair.Key), currentElement = FindOne(target.Root, pair.Key)
            For Each name In pair.Value
                Dim value = originalElement.Attribute(name)
                If value IsNot Nothing Then currentElement.SetAttributeValue(name, value.Value) ' Absent in the original: keep DiRT 3's.
            Next
        Next
        Dim rate = FindOne(source.Root, RefreshRatePath)
        If rate IsNot Nothing Then
            Dim resolution = FindOne(target.Root, "graphics_card/resolution")
            resolution.Elements("refreshRate").Remove()
            resolution.Add(New XElement(rate))
        End If
        Dim declaration = If(target.Declaration Is Nothing, "", target.Declaration.ToString())
        Return Encoding.UTF8.GetBytes(declaration & target.ToString(SaveOptions.DisableFormatting))
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

    Public Function Changes(context As InstallContext, Optional width As Integer = 1280, Optional height As Integer = 720, Optional skipStartupMovies As Boolean = False, Optional vrQuality As IDictionary(Of String, String) = Nothing) As IReadOnlyDictionary(Of String, Byte())
        Dim payload = Path.Combine(context.PackageRoot, "payload", "d3d11.dll")
        For Each target In {context.GraphicsPath, Path.Combine(context.GameRoot, "system", "hardware_settings_restrictions.xml"), payload}
            SafeFiles.NoLinks(target)
            If Not File.Exists(target) Then Throw New IOException("Required experimental VR file is missing: " & target)
        Next
        Dim preparedFiles As New Dictionary(Of String, Byte()) From {
            {"proxy", File.ReadAllBytes(payload)}, {"graphics", Graphics(File.ReadAllBytes(context.GraphicsPath), width, height, vrQuality)},
            {"restrictions", Restrictions(File.ReadAllBytes(Path.Combine(context.GameRoot, "system", "hardware_settings_restrictions.xml")))}}
        If skipStartupMovies Then preparedFiles.Add("startup", StartupMovies.ReadPatch(context))
        Return preparedFiles
    End Function
End Module

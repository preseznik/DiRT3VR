Imports Microsoft.Win32
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Text.RegularExpressions

' OpenXR add-ons ("API layers" such as ReShade or the OBS mirror) load into every OpenXR app.
' One without a working 32-bit library stops DiRT 3's VR from starting. The launcher names it
' and, only with the player's approval, turns it off for DiRT 3 alone through the add-on's own
' disable variable. Adapted from DiRT2VR (launcher/VrApiLayers.vb, VrPreflight.vb).
Public NotInheritable Class VrLayerBypass
    Public Property LayerName As String = ""
    Public Property ManifestPath As String = ""
    Public Property ManifestHash As String = ""
    Public Property LibraryPath As String = ""
    Public Property DisableVariable As String = ""

    Public ReadOnly Property DisplayName As String
        Get
            Select Case LayerName
                Case "XR_APILAYER_reshade" : Return "ReShade"
                Case "XR_APILAYER_NOVENDOR_OBSMirror" : Return "OpenXR OBS Mirror"
                Case Else : Return LayerName
            End Select
        End Get
    End Property

    Public ReadOnly Property Consequence As String
        Get
            Select Case LayerName
                Case "XR_APILAYER_reshade" : Return "ReShade's OpenXR effects won't apply to DiRT 3."
                Case "XR_APILAYER_NOVENDOR_OBSMirror" : Return "OBS Mirror won't capture DiRT 3's VR view."
                Case Else : Return "Whatever this OpenXR add-on provides won't apply to DiRT 3."
            End Select
        End Get
    End Property

    ' The same registration, manifest bytes, library and switch; anything else needs a new approval.
    Public Function SameIdentity(other As VrLayerBypass) As Boolean
        Return other IsNot Nothing AndAlso LayerName = other.LayerName AndAlso
            String.Equals(ManifestPath, other.ManifestPath, StringComparison.OrdinalIgnoreCase) AndAlso ManifestHash = other.ManifestHash AndAlso
            String.Equals(LibraryPath, other.LibraryPath, StringComparison.OrdinalIgnoreCase) AndAlso DisableVariable = other.DisableVariable
    End Function

    Public Overrides Function ToString() As String
        Return DisplayName
    End Function
End Class

Public NotInheritable Class VrLayerFailure
    Public Property LayerName As String
    Public Property LibraryPath As String
    Public Property WindowsError As Integer
End Class

Public NotInheritable Class VrApiLayers
    Private Const RegistryPath As String = "SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit"
    ' XR_ERROR_FILE_ACCESS_ERROR: the loader could not open an add-on's library.
    Public Const LayerLoadFailure As Integer = -32

    Private Sub New()
    End Sub

    ' Never let a manifest turn a bypass into PATH, runtime or other process configuration.
    Public Shared Function SafeDisableVariable(value As String) As Boolean
        Return value IsNot Nothing AndAlso Regex.IsMatch(value, "\ADISABLE_(?:XR_|OPENXR_)[A-Za-z0-9_]{1,180}\z")
    End Function

    Public Shared Function ReadManifest(path As String) As VrLayerBypass
        Try
            If Not IO.Path.IsPathFullyQualified(path) Then Return Nothing
            SafeFiles.NoLinks(path)
            Using stream = File.OpenRead(path)
                If stream.Length > 65536 Then Return Nothing
                Dim bytes(CInt(stream.Length) - 1) As Byte
                stream.ReadExactly(bytes)
                Using document = JsonDocument.Parse(bytes)
                    Dim layer = document.RootElement.GetProperty("api_layer")
                    Dim name = layer.GetProperty("name").GetString()
                    Dim library = layer.GetProperty("library_path").GetString()
                    Dim variable = layer.GetProperty("disable_environment").GetString()
                    If name Is Nothing OrElse Not Regex.IsMatch(name, "\AXR_APILAYER_[A-Za-z0-9_]{1,200}\z") OrElse String.IsNullOrWhiteSpace(library) OrElse Not SafeDisableVariable(variable) Then Return Nothing
                    Return New VrLayerBypass With {.LayerName = name, .ManifestPath = IO.Path.GetFullPath(path), .ManifestHash = Convert.ToHexString(SHA256.HashData(bytes)),
                        .LibraryPath = IO.Path.GetFullPath(IO.Path.Combine(IO.Path.GetDirectoryName(path), library)), .DisableVariable = variable}
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.SecurityException OrElse
                                   TypeOf ex Is JsonException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is KeyNotFoundException OrElse
                                   TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is NotSupportedException
            Return Nothing
        End Try
    End Function

    ' The enabled implicit add-ons the 32-bit game sees (both hives, 32-bit registry view).
    Public Shared Function Discover() As List(Of VrLayerBypass)
        Dim paths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each hive In {RegistryHive.LocalMachine, RegistryHive.CurrentUser}
            Try
                Using root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32), key = root.OpenSubKey(RegistryPath)
                    If key Is Nothing Then Continue For
                    For Each path In key.GetValueNames()
                        If key.GetValueKind(path) = RegistryValueKind.DWord AndAlso CInt(key.GetValue(path)) = 0 Then paths.Add(path)
                    Next
                End Using
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.SecurityException
                ' An incomplete inventory must not authorize any bypass.
                Return New List(Of VrLayerBypass)()
            End Try
        Next
        Return paths.Select(Function(path) ReadManifest(path)).Where(Function(layer) layer IsNot Nothing).ToList()
    End Function

    ' Add-ons the loader could not open, from the runtime probe's report (its JSON line plus the
    ' loader's own messages). Loader text is fixed by the pinned OpenXR SDK; Windows errors are
    ' matched by number, not by their localized description.
    Public Shared Function Failures(exitCode As Integer, report As String) As List(Of VrLayerFailure)
        Dim result As New List(Of VrLayerFailure)()
        If exitCode = 0 OrElse report Is Nothing OrElse Not Regex.IsMatch(report, """operation"":""createInstance"",""xrResult"":" & LayerLoadFailure & "[,}]") OrElse
           Not report.Contains("Failed loading layer information", StringComparison.Ordinal) Then Return result
        For Each match As Match In Regex.Matches(report, "ApiLayerInterface::LoadApiLayers skipping layer (?<name>XR_APILAYER_[A-Za-z0-9_]+), failed to load with message ""Failed to open dynamic library (?<path>[^\r\n]+?) with error (?<error>[0-9]+):")
            Dim code As Integer
            If Not Integer.TryParse(match.Groups("error").Value, code) Then Continue For
            Dim path As String
            Try
                If Not IO.Path.IsPathFullyQualified(match.Groups("path").Value) Then Continue For
                path = IO.Path.GetFullPath(match.Groups("path").Value)
            Catch ex As ArgumentException
                Continue For
            End Try
            If result.Any(Function(item) item.LayerName = match.Groups("name").Value AndAlso String.Equals(item.LibraryPath, path, StringComparison.OrdinalIgnoreCase)) Then Continue For
            result.Add(New VrLayerFailure With {.LayerName = match.Groups("name").Value, .LibraryPath = path, .WindowsError = code})
        Next
        Return result
    End Function

    ' Each failure must match exactly one registered add-on by name and library, with a switch of its own.
    Public Shared Function Resolve(failures As IReadOnlyList(Of VrLayerFailure), catalog As IReadOnlyList(Of VrLayerBypass)) As List(Of VrLayerBypass)
        Dim result As New List(Of VrLayerBypass)()
        If failures.Count = 0 OrElse failures.Count > 16 Then Return result
        For Each failure In failures
            Dim matches = catalog.Where(Function(layer) layer.LayerName = failure.LayerName).ToArray()
            If matches.Length <> 1 OrElse Not String.Equals(matches(0).LibraryPath, failure.LibraryPath, StringComparison.OrdinalIgnoreCase) OrElse Not SafeDisableVariable(matches(0).DisableVariable) Then Return New List(Of VrLayerBypass)()
            ' A shared switch would also turn off an add-on the failure did not name.
            If catalog.Any(Function(layer) String.Equals(layer.DisableVariable, matches(0).DisableVariable, StringComparison.OrdinalIgnoreCase) AndAlso layer.LayerName <> matches(0).LayerName) Then Return New List(Of VrLayerBypass)()
            result.Add(matches(0))
        Next
        Return result
    End Function

    ' Remembered approvals apply only while the registration is unchanged.
    Public Shared Function Revalidate(saved As IEnumerable(Of VrLayerBypass), catalog As IReadOnlyList(Of VrLayerBypass)) As List(Of VrLayerBypass)
        Dim result As New List(Of VrLayerBypass)()
        For Each entry In saved.Where(Function(item) item IsNot Nothing)
            Dim resolved = Resolve({New VrLayerFailure With {.LayerName = entry.LayerName, .LibraryPath = entry.LibraryPath}}, catalog)
            If resolved.Count = 1 AndAlso resolved(0).SameIdentity(entry) AndAlso Not result.Any(Function(item) item.SameIdentity(entry)) Then result.Add(resolved(0))
        Next
        Return result
    End Function

    Public Shared Sub Configure(start As ProcessStartInfo, bypasses As IEnumerable(Of VrLayerBypass))
        If bypasses Is Nothing Then Return
        For Each layer In bypasses
            If Not SafeDisableVariable(layer.DisableVariable) Then Throw New IOException("Invalid OpenXR add-on disable switch.")
            start.Environment(layer.DisableVariable) = "1"
        Next
    End Sub

    Public Shared Function Describe(failures As IEnumerable(Of VrLayerFailure)) As String
        Return "An OpenXR add-on could not load: " & String.Join("; ", failures.Select(Function(layer) layer.LayerName & " (Windows error " & layer.WindowsError & ")")) & "."
    End Function
End Class

Public NotInheritable Class VrLayerPreferences
    Public Property Version As Integer = 1
    Public Property Bypasses As New List(Of VrLayerBypass)()

    Private Shared Function PathFor(context As InstallContext) As String
        Return Path.Combine(context.UserRoot, "vr-layer-bypasses.json")
    End Function

    Public Shared Function Load(context As InstallContext) As VrLayerPreferences
        Try
            Dim stored = PathFor(context)
            If File.Exists(stored) Then
                Dim saved = SafeFiles.ReadJson(Of VrLayerPreferences)(stored)
                If saved IsNot Nothing AndAlso saved.Version = 1 AndAlso saved.Bypasses IsNot Nothing Then
                    saved.Bypasses = saved.Bypasses.Where(Function(item) item IsNot Nothing).Take(64).ToList()
                    Return saved
                End If
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException
            ' Invalid preferences grant no permission to turn off an add-on.
        End Try
        Return New VrLayerPreferences()
    End Function

    Public Sub Save(context As InstallContext)
        SafeFiles.SaveJson(PathFor(context), Me)
    End Sub

    Public Shared Sub Remember(context As InstallContext, layers As IEnumerable(Of VrLayerBypass))
        Dim saved = Load(context)
        For Each layer In layers
            saved.Bypasses.RemoveAll(Function(item) item.LayerName = layer.LayerName)
            saved.Bypasses.Add(layer)
        Next
        saved.Save(context)
    End Sub

    Public Shared Sub Reenable(context As InstallContext, layerName As String)
        Dim saved = Load(context)
        saved.Bypasses.RemoveAll(Function(item) item.LayerName = layerName)
        saved.Save(context)
    End Sub
End Class

' A failed runtime probe, with its exit code and full output for add-on diagnosis.
Public NotInheritable Class RuntimeProbeException
    Inherits IOException
    Public ReadOnly Property ExitCode As Integer
    Public ReadOnly Property Report As String

    Public Sub New(message As String, exitCode As Integer, report As String, Optional inner As Exception = Nothing)
        MyBase.New(message, inner)
        Me.ExitCode = exitCode : Me.Report = If(report, "")
    End Sub
End Class

Public NotInheritable Class VrLayerRecoveryRequest
    Public Property Layers As IReadOnlyList(Of VrLayerBypass)
    Public Property Report As String
End Class

Public NotInheritable Class VrLayerRecoveryChoice
    Public Property Retry As Boolean
    Public Property Remember As Boolean
End Class

Public NotInheritable Class VrPreflightResult
    Public Property Headset As HeadsetResolution
    Public Property Bypasses As New List(Of VrLayerBypass)()
    Public Property Warning As String = ""
End Class

' Runs the runtime probe with remembered bypasses; if an add-on blocks it, asks before retrying without it.
Public NotInheritable Class VrLayerPreflight
    Private Sub New()
    End Sub

    Public Shared Async Function ReadHeadsetAsync(context As InstallContext, run As Func(Of IReadOnlyList(Of VrLayerBypass), Task(Of HeadsetResolution)),
                                                  confirm As Func(Of VrLayerRecoveryRequest, VrLayerRecoveryChoice),
                                                  Optional discover As Func(Of List(Of VrLayerBypass)) = Nothing) As Task(Of VrPreflightResult)
        If discover Is Nothing Then discover = AddressOf VrApiLayers.Discover
        Dim catalog = discover()
        Dim bypasses = VrApiLayers.Revalidate(VrLayerPreferences.Load(context).Bypasses, catalog)
        Dim first As RuntimeProbeException = Nothing
        Try
            Return New VrPreflightResult With {.Headset = Await run(bypasses), .Bypasses = bypasses}
        Catch ex As RuntimeProbeException
            first = ex
        End Try
        Dim failures = VrApiLayers.Failures(first.ExitCode, first.Report)
        If failures.Count = 0 Then Throw first
        Dim candidates = VrApiLayers.Resolve(failures, catalog)
        Dim problem = VrApiLayers.Describe(failures)
        If candidates.Count = 0 OrElse candidates.Any(Function(layer) bypasses.Any(Function(item) item.SameIdentity(layer))) Then
            Throw New IOException(problem & " DiRT3VR can't turn it off automatically; remove or repair that add-on, or ask its author for a 32-bit OpenXR layer.", first)
        End If
        If confirm Is Nothing Then Throw New IOException(problem & " Launch VR from the launcher window to approve turning it off for DiRT 3.", first)
        Dim choice = confirm(New VrLayerRecoveryRequest With {.Layers = candidates, .Report = first.Report})
        If choice Is Nothing OrElse Not choice.Retry Then Throw New IOException(problem & " VR was not started; the add-on stays on.", first)
        ' Re-read after the dialog: never apply an approval to a registration changed while it was open.
        Dim approved = bypasses.Concat(candidates).ToList()
        If VrApiLayers.Revalidate(approved, discover()).Count <> approved.Count Then Throw New IOException("The OpenXR add-on registration changed. Launch VR again to review it.")
        Dim headset As HeadsetResolution = Nothing, retryError As RuntimeProbeException = Nothing
        Try
            headset = Await run(approved)
        Catch ex As RuntimeProbeException
            retryError = ex
        End Try
        If retryError IsNot Nothing Then
            Throw New IOException("VR still could not start with " & String.Join(", ", candidates.Select(Function(layer) layer.DisplayName)) & " turned off. " & retryError.Message & Environment.NewLine & "Original problem: " & problem, retryError)
        End If
        Dim result As New VrPreflightResult With {.Headset = headset, .Bypasses = approved}
        If choice.Remember Then
            Try
                VrLayerPreferences.Remember(context, candidates)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                result.Warning = "The add-on is off for this session, but the choice could not be remembered: " & ex.Message
            End Try
        End If
        Return result
    End Function
End Class

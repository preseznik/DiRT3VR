Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports Microsoft.Win32

' The player's own controls from DiRT 3's profile save (#cb2fa6e5, docs/research/driving-controls.md). DiRT 3 keeps
' them in Steam Cloud (userdata\<account>\321040\remote\savegame@profile0#…), AES-256 in ECB mode with one key for
' every install (logged from the game's own key setup, checked on two PCs and three profiles). A direct start never
' loads the profile, so direct practice starts each device's session map from these instead of DiRT 3's stock preset.
' Read only, never written: the controls file is found by the magic in its first block, so the other profile files
' (player names, accounts, progress) are never decrypted beyond their first 16 bytes.
Public NotInheritable Class ProfileControls
    Public Const Magic As UInteger = &HEFF0FFUI
    Public Const MaxFileBytes As Integer = 65536
    Private Const KeyHex As String = "BB39A6C0D71EC17E25D1E3CDB69272C439CC1407CCC668D69AD72854DE48883E"
    ' The save's calibration numbers, as DiRT 3's map files name them (0, 2 and 3 checked against the stock maps).
    Public Shared ReadOnly Calibrations As String() = {"uniDirectionalPositive", "uniDirectionalNegative", "biDirectionalUpper", "biDirectionalLower"}

    Public NotInheritable Class ProfileInput
        Public Property Action As String = ""
        Public Property Axis As String = ""
        Public Property Type As String = ""
        Public Property DeadZone As Single
        Public Property Saturation As Single
        Public Property Device As String = ""
    End Class

    ' DiRT 3's device names, as in its action maps ("xinput_pad", "keyboard", a wheel's product name).
    Public ReadOnly Property Devices As New List(Of String)
    ' Every action the profile lists, bound or not.
    Public ReadOnly Property Actions As New HashSet(Of String)(StringComparer.Ordinal)
    Public ReadOnly Property Inputs As New List(Of ProfileInput)
    ' The file it came from, for the session log.
    Public Property Source As String = ""

    Private Sub New()
    End Sub

    Private Shared Function Cipher() As Aes
        Dim aes = Security.Cryptography.Aes.Create()
        aes.Key = Convert.FromHexString(KeyHex)
        aes.Mode = CipherMode.ECB
        aes.Padding = PaddingMode.None
        Return aes
    End Function

    Public Shared Function Decrypt(data As Byte()) As Byte()
        If data Is Nothing OrElse data.Length = 0 OrElse data.Length Mod 16 <> 0 Then Throw New IOException("A DiRT 3 profile file has an invalid size.")
        Using aes = Cipher()
            Return aes.DecryptEcb(data, PaddingMode.None)
        End Using
    End Function

    ' For tests: the save's own encryption.
    Public Shared Function Encrypt(plain As Byte()) As Byte()
        If plain Is Nothing OrElse plain.Length = 0 OrElse plain.Length Mod 16 <> 0 Then Throw New ArgumentException("Whole 16-byte blocks only.", NameOf(plain))
        Using aes = Cipher()
            Return aes.EncryptEcb(plain, PaddingMode.None)
        End Using
    End Function

    ' Whether an encrypted file starts with the controls file's magic (only its first block is decrypted).
    Public Shared Function IsControls(data As Byte()) As Boolean
        If data Is Nothing OrElse data.Length < 16 OrElse data.Length Mod 16 <> 0 Then Return False
        Return BitConverter.ToUInt32(Decrypt(data.AsSpan(0, 16).ToArray()), 0) = Magic
    End Function

    ' The decrypted controls file: magic, devices, then each action with its inputs (axis, type, dead zone,
    ' saturation, threshold, device index). Anything out of shape is refused.
    Public Shared Function Parse(plain As Byte()) As ProfileControls
        Dim position = 0
        Dim fail = Function() As Exception
                       Return New IOException("The DiRT 3 profile's controls can't be read.")
                   End Function
        Dim u32 = Function() As UInteger
                      If position + 4 > plain.Length Then Throw fail()
                      Dim value = BitConverter.ToUInt32(plain, position)
                      position += 4
                      Return value
                  End Function
        Dim f32 = Function() As Single
                      If position + 4 > plain.Length Then Throw fail()
                      Dim value = BitConverter.ToSingle(plain, position)
                      position += 4
                      If Single.IsNaN(value) OrElse value < 0F OrElse value > 1F Then Throw fail()
                      Return value
                  End Function
        Dim text = Function() As String
                       Dim length = u32()
                       If length < 2 OrElse length > 261 OrElse position + CInt(length) > plain.Length OrElse plain(position + CInt(length) - 1) <> 0 Then Throw fail()
                       Dim bytes = plain.AsSpan(position, CInt(length) - 1)
                       For Each value In bytes
                           If value < 32 OrElse value > 126 Then Throw fail()
                       Next
                       position += CInt(length)
                       Return Encoding.ASCII.GetString(bytes)
                   End Function
        If plain Is Nothing OrElse plain.Length < 16 OrElse u32() <> Magic Then Throw fail()
        Dim result As New ProfileControls()
        Dim deviceCount = u32()
        If deviceCount < 1 OrElse deviceCount > 32 Then Throw fail()
        For i = 1 To CInt(deviceCount)
            result.Devices.Add(text())
        Next
        Dim actionCount = u32()
        If actionCount < 1 OrElse actionCount > 256 Then Throw fail()
        For i = 1 To CInt(actionCount)
            Dim action = text()
            result.Actions.Add(action)
            Dim inputCount = u32()
            If inputCount > 16 Then Throw fail()
            For j = 1 To CInt(inputCount)
                Dim axis = text()
                Dim type = u32()
                Dim deadZone = f32(), saturation = f32()
                f32() ' threshold
                Dim device = u32()
                If type >= Calibrations.Length OrElse device >= deviceCount Then Throw fail()
                result.Inputs.Add(New ProfileInput With {.Action = action, .Axis = axis, .Type = Calibrations(CInt(type)), .DeadZone = deadZone, .Saturation = saturation, .Device = result.Devices(CInt(device))})
            Next
        Next
        Return result
    End Function

    ' The controls in one Steam Cloud folder (…\321040\remote), or Nothing when it has none.
    Public Shared Function FromFolder(folder As String) As ProfileControls
        If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return Nothing
        For Each file In Directory.EnumerateFiles(folder, "savegame@profile0#*")
            Dim info As New FileInfo(file)
            If info.Length < 16 OrElse info.Length > MaxFileBytes OrElse info.Length Mod 16 <> 0 OrElse info.Attributes.HasFlag(FileAttributes.ReparsePoint) Then Continue For
            Dim data = IO.File.ReadAllBytes(file)
            If Not IsControls(data) Then Continue For
            Dim result = Parse(Decrypt(data))
            result.Source = file
            Return result
        Next
        Return Nothing
    End Function

    ' The signed-in Steam account's DiRT 3 controls (Steam's ActiveProcess\ActiveUser), else the only account with a
    ' DiRT 3 profile, else the most recently saved one; Nothing when there's none or it can't be read.
    Public Shared Function ForCurrentUser() As ProfileControls
        Try
            Dim steam As String = Nothing, active As Integer
            Using key = Registry.CurrentUser.OpenSubKey("Software\Valve\Steam")
                steam = TryCast(key?.GetValue("SteamPath"), String)
            End Using
            Using key = Registry.CurrentUser.OpenSubKey("Software\Valve\Steam\ActiveProcess")
                Dim value = key?.GetValue("ActiveUser")
                If TypeOf value Is Integer Then active = CInt(value)
            End Using
            If String.IsNullOrWhiteSpace(steam) Then steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
            Dim userdata = Path.Combine(steam.Replace("/", "\"), "userdata")
            If Not Directory.Exists(userdata) Then Return Nothing
            Dim remote = Function(account As String) Path.Combine(userdata, account, InstallContext.SteamAppId, "remote")
            If active > 0 AndAlso Directory.Exists(remote(active.ToString(Globalization.CultureInfo.InvariantCulture))) Then
                Return FromFolder(remote(active.ToString(Globalization.CultureInfo.InvariantCulture)))
            End If
            Dim found = Directory.EnumerateDirectories(userdata).Select(Function(folder) FromFolder(Path.Combine(folder, InstallContext.SteamAppId, "remote"))).
                Where(Function(controls) controls IsNot Nothing).OrderByDescending(Function(controls) IO.File.GetLastWriteTimeUtc(controls.Source)).ToList()
            Return found.FirstOrDefault()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.SecurityException OrElse TypeOf ex Is CryptographicException
            Return Nothing
        End Try
    End Function

    ' One device's inputs for an action, in the profile's order.
    Public Function InputsFor(action As String, device As String) As IEnumerable(Of ProfileInput)
        Return Inputs.Where(Function(input) input.Action = action AndAlso input.Device = device)
    End Function
End Class

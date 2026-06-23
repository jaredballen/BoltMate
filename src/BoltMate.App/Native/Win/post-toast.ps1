param(
    [Parameter(Mandatory=$true)] [string] $Title,
    [Parameter(Mandatory=$true)] [string] $Body,
    [string] $Aumid = 'BoltMate'
)

$ErrorActionPreference = 'Stop'

# Force-load the WinRT projection so the Notifications types resolve
# under Win PowerShell 5.x (where they don't auto-load).
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null

# ToastText02 = single title line + one wrapped body line.
$tpl = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent(
    [Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$tn  = $tpl.GetElementsByTagName('text')
$tn.Item(0).AppendChild($tpl.CreateTextNode($Title)) | Out-Null
$tn.Item(1).AppendChild($tpl.CreateTextNode($Body))  | Out-Null

$toast    = [Windows.UI.Notifications.ToastNotification]::new($tpl)
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($Aumid)
$notifier.Show($toast)

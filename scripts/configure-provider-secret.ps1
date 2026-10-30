[CmdletBinding()]
param(
  [ValidateSet('minimax','a')][string]$Provider='minimax',
  [string]$CodexHome=(Join-Path $env:USERPROFILE '.codex'),
  [Parameter(Mandatory=$true)][string]$StatusPath
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName System.Security
$credentialDirectory=Join-Path $CodexHome 'secrets'
$credentialFile=Join-Path $credentialDirectory ('control-center-'+$Provider+'.dpapi')
$providerLabel=if($Provider -eq 'minimax'){'MiniMax'}else{'114514 / Key A'}
$credentialWindow=New-Object Windows.Window
$credentialWindow.Title=$providerLabel+' · 安全配置密钥'
$credentialWindow.Width=700;$credentialWindow.Height=450;$credentialWindow.WindowStartupLocation='CenterScreen'
$credentialWindow.ResizeMode='NoResize'
$credentialPanel=New-Object Windows.Controls.StackPanel
$credentialPanel.Margin='30'
$credentialWindow.Content=$credentialPanel
function Add-Label([string]$Content,[double]$Size=18){
 $label=New-Object Windows.Controls.TextBlock;$label.Text=$Content;$label.FontSize=$Size;$label.TextWrapping='Wrap';$label.Margin='0,0,0,16';$credentialPanel.Children.Add($label)|Out-Null
}
Add-Label ($providerLabel+' API Key') 26
Add-Label '请在服务商控制台撤销泄露的旧 Key，再将新 Key 粘贴到下方。内容只保存为当前 Windows 用户的 DPAPI 加密文件，不写入聊天或命令参数。'
$credentialPassword=New-Object Windows.Controls.PasswordBox
$credentialPassword.FontSize=20;$credentialPassword.Height=48;$credentialPassword.Margin='0,0,0,16'
$credentialPanel.Children.Add($credentialPassword)|Out-Null
$credentialMessage=New-Object Windows.Controls.TextBlock
$credentialMessage.FontSize=16;$credentialMessage.TextWrapping='Wrap';$credentialMessage.Margin='0,0,0,16'
$credentialPanel.Children.Add($credentialMessage)|Out-Null
$credentialSave=New-Object Windows.Controls.Button
$credentialSave.Content='加密保存';$credentialSave.Height=48;$credentialSave.FontSize=20
$credentialPanel.Children.Add($credentialSave)|Out-Null
$credentialSave.Add_Click({
 $bytes=$null;$ciphertext=$null
 try{
  $value=$credentialPassword.Password.Trim()
  if($value.Length -lt 20 -or $value -match '\s'){ $credentialMessage.Text='密钥格式不正确，请检查后重试。';return }
  $bytes=[Text.Encoding]::UTF8.GetBytes($value)
  $ciphertext=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
  [IO.Directory]::CreateDirectory($credentialDirectory)|Out-Null
  $temporary=$credentialFile+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
  [IO.File]::WriteAllText($temporary,[Convert]::ToBase64String($ciphertext))
  if([IO.File]::Exists($credentialFile)){[IO.File]::Replace($temporary,$credentialFile,($credentialFile+'.previous-'+[Guid]::NewGuid().ToString('N')+'.dpapi'))}else{[IO.File]::Move($temporary,$credentialFile)}
  $credentialPassword.Clear();$value=$null
  [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($StatusPath))|Out-Null
  @{saved=$true;provider=$Provider;credential_file=$credentialFile;time=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Compress|Set-Content -LiteralPath $StatusPath -Encoding UTF8
  $credentialWindow.Close()
 }catch{ $credentialMessage.Text='加密保存失败，请检查当前 Windows 用户权限。没有显示密钥或异常详情。' }
 finally{if($bytes){[Array]::Clear($bytes,0,$bytes.Length)};if($ciphertext){[Array]::Clear($ciphertext,0,$ciphertext.Length)}}
})
$credentialWindow.Add_ContentRendered({$credentialPassword.Focus()|Out-Null})
$credentialWindow.ShowDialog()|Out-Null

# Captures the Dicey Dungeons window to a PNG so a screen can be verified
# without a person watching the monitor.
param([string]$Path, [int]$Width = 1100)
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace S -Name N -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string win);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
'@
$h = [S.N]::FindWindow("SDL_app", "Dicey Dungeons")
if ($h -eq [IntPtr]::Zero) { Write-Output "NOWINDOW"; exit 1 }
$r = New-Object S.N+RECT
[void][S.N]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $ht = $r.B - $r.T
if ($w -le 0 -or $ht -le 0) { Write-Output "BADRECT"; exit 1 }
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$g.Dispose()
if ($w -gt $Width) {
  $nh = [int]($ht * $Width / $w)
  $small = New-Object System.Drawing.Bitmap $Width, $nh
  $g2 = [System.Drawing.Graphics]::FromImage($small)
  $g2.InterpolationMode = 'HighQualityBicubic'
  $g2.DrawImage($bmp, 0, 0, $Width, $nh); $g2.Dispose(); $bmp.Dispose(); $bmp = $small
}
$bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "SAVED $Path ($w x $ht)"

Add-Type -TypeDefinition @"
using System;using System.IO;using System.Runtime.InteropServices;using Microsoft.Win32.SafeHandles;
public class FI {
  [StructLayout(LayoutKind.Sequential, Pack=1)] public struct BHF {
    public uint FileAttributes; public long CreationTime, LastAccessTime, LastWriteTime;
    public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
  }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern SafeFileHandle CreateFileW(string p,uint acc,uint share,IntPtr sec,uint disp,uint flags,IntPtr tmpl);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool GetFileInformationByHandle(SafeFileHandle h, out BHF info);
  public static string Id(string path, out uint links, out ulong idx) {
    links=0; idx=0;
    var h = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
    if (h.IsInvalid) return null;
    BHF b; if(!GetFileInformationByHandle(h, out b)) { h.Dispose(); return null; }
    h.Dispose();
    links=b.NumberOfLinks; idx=((ulong)b.FileIndexHigh<<32)|b.FileIndexLow;
    return b.VolumeSerialNumber.ToString("X8");
  }
}
"@
$installs = @(
 @{n='B';        p='C:\AlterumB'},
 @{n='E';        p='C:\AlterumE'},
 @{n='I';        p='C:\AlterumI'},
 @{n='SA';       p='C:\AlterumSA'},
 @{n='T1';       p='C:\AlterumT1'},
 @{n='T2';       p='C:\AlterumT2'},
 @{n='T3';       p='C:\AlterumT3'},
 @{n='Pinned';   p='C:\AlterumPinned'},
 @{n='Pristine'; p='C:\AlterumPinned-pristine'},
 @{n='Steam';    p='C:\Program Files (x86)\Steam\steamapps\common\Alterum'},
 @{n='Cand';     p='C:\AlterumCandidates\devbuild_unstable'}
)
$rows = New-Object System.Collections.Generic.List[object]
foreach ($e in $installs) {
  if (-not (Test-Path $e.p)) { continue }
  Get-ChildItem $e.p -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
    $l=0; $idx=0
    $vol = [FI]::Id($_.FullName, [ref]$l, [ref]$idx)
    if ($vol) {
      $rows.Add([pscustomobject]@{ inst=$e.n; vol=$vol; idx=$idx; links=$l; len=$_.Length; rel=$_.FullName.Substring($e.p.Length+1) })
    }
  }
}
$rows | Export-Csv -Path (Join-Path $env:TEMP 'alterum-audit\census2.csv') -NoTypeInformation -Encoding UTF8
"census rows: $($rows.Count)"

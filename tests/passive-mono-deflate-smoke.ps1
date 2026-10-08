# Independent pure-memory compression only, using the game's actual
# MonoPosixHelper library exports. No Unity, real Book/Unit, save model,
# constructor, singleton, save file, game process, or game executable is used.
# The callback matches the game's System.dll DeflateStreamNative IL contract.
param(
    [string]$GameDir='D:\game\steamapps\common\Library Of Ruina',
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$nativeDll=(Resolve-Path -LiteralPath (Join-Path $GameDir 'MonoBleedingEdge\EmbedRuntime\MonoPosixHelper.dll')).ProviderPath
if([IntPtr]::Size -ne 8){throw 'Run this read-only compression smoke in a 64-bit PowerShell process.'}
$nativeSource=@'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
public static class RuinaPassiveMonoDeflateSmoke {
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadCallback(IntPtr buffer,int count,IntPtr data);
    [DllImport(@"__NATIVE_DLL__",CallingConvention=CallingConvention.Cdecl)]
    private static extern IntPtr CreateZStream(int mode,bool gzip,ReadCallback callback,IntPtr data);
    [DllImport(@"__NATIVE_DLL__",CallingConvention=CallingConvention.Cdecl)]
    private static extern int ReadZStream(IntPtr handle,IntPtr buffer,int count);
    [DllImport(@"__NATIVE_DLL__",CallingConvention=CallingConvention.Cdecl)]
    private static extern int CloseZStream(IntPtr handle);
    private sealed class Input { internal byte[] Bytes;internal int Position;internal bool ReadPastEnd; }
    public sealed class Result {
        public string Case;public int RawLength,PackedLength,Produced,ExtraResult,Consumed;public bool ReadPastEnd,SameData,Accept;
    }
    private static int ReadInput(IntPtr buffer,int count,IntPtr token) {
        var input=(Input)GCHandle.FromIntPtr(token).Target;
        if(count<=0)return 0;
        if(input.Position==input.Bytes.Length){input.ReadPastEnd=true;return 0;}
        Marshal.Copy(input.Bytes,input.Position,buffer,1);input.Position++;return 1;
    }
    private static byte[] Pack(byte[] raw) {
        using(var stream=new MemoryStream()) {
            using(var deflate=new DeflateStream(stream,CompressionLevel.Optimal,true))deflate.Write(raw,0,raw.Length);
            return stream.ToArray();
        }
    }
    private static Result Probe(string name,byte[] raw,byte[] packed) {
        var input=new Input{Bytes=packed};var token=GCHandle.Alloc(input);ReadCallback callback=ReadInput;
        var stream=IntPtr.Zero;var buffer=IntPtr.Zero;
        try {
            stream=CreateZStream(0,false,callback,GCHandle.ToIntPtr(token));
            if(stream==IntPtr.Zero)throw new IOException("Native decompressor allocation failed.");
            buffer=Marshal.AllocHGlobal(raw.Length+1);
            int produced=0,result=0;
            while(produced<raw.Length){result=ReadZStream(stream,IntPtr.Add(buffer,produced),raw.Length-produced);if(result<=0)break;produced+=result;}
            var extra=result<0?result:ReadZStream(stream,IntPtr.Add(buffer,raw.Length),1);
            var actual=new byte[produced];Marshal.Copy(buffer,actual,0,actual.Length);bool same=produced==raw.Length;
            for(int i=0;i<actual.Length&&same;i++)same=actual[i]==raw[i];
            return new Result{Case=name,RawLength=raw.Length,PackedLength=packed.Length,Produced=produced,ExtraResult=extra,
                Consumed=input.Position,ReadPastEnd=input.ReadPastEnd,SameData=same,
                Accept=same&&extra==0&&!input.ReadPastEnd&&input.Position==packed.Length};
        } finally {
            try {if(stream!=IntPtr.Zero)CloseZStream(stream);}
            finally {if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);token.Free();GC.KeepAlive(callback);}
        }
    }
    public static Result[] Run() {
        var results=new List<Result>();
        foreach(int length in new[]{2,121,61111,131074,4000000}) {
            var raw=new byte[length];for(int i=0;i<length;i++)raw[i]=(byte)((i%121<108)?(i%12==0?255:0):(i*7)%256);
            var packed=Pack(raw);results.Add(Probe("valid-"+length,raw,packed));
            var truncated=new byte[packed.Length-1];Array.Copy(packed,truncated,truncated.Length);
            results.Add(Probe("truncated-"+length,raw,truncated));
            var tail=new byte[packed.Length+1];Array.Copy(packed,tail,packed.Length);tail[tail.Length-1]=34;
            results.Add(Probe("trailing-"+length,raw,tail));
        }
        return results.ToArray();
    }
}
'@
# Only a C# verbatim DLL path is substituted; quote characters are escaped.
Add-Type -TypeDefinition ($nativeSource.Replace('__NATIVE_DLL__',$nativeDll.Replace('"','""'))) -ReferencedAssemblies 'System.dll'
$samples=[RuinaPassiveMonoDeflateSmoke]::Run()
$failed=@($samples | Where-Object { ($_.Case.StartsWith('valid-') -and -not $_.Accept) -or (-not $_.Case.StartsWith('valid-') -and $_.Accept) })
$result=[pscustomobject]@{Success=$failed.Count -eq 0;Execution='Independent pure-memory native compression probe. Actual game MonoPosixHelper.dll exports; no Unity/game models/saves/constructors/singletons or game process.';
    NativeFileHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $nativeDll).Hash;
    Samples=$samples;Failures=$failed}
if($OutputPath){$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding UTF8}
if($failed.Count -gt 0){throw "Native Mono Deflate boundary failed in $($failed.Count) cases."}
Write-Output "PASS: $($samples.Count) actual game MonoPosixHelper pure-memory valid/truncated/trailing decompression boundaries."
Write-Output "Native DLL SHA256: $($result.NativeFileHash)"

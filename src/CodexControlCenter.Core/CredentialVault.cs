using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexControlCenter.Core;

/// <summary>Current-user DPAPI. Plaintext never enters config, process arguments or diagnostics.</summary>
public static class CredentialVault
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError=true, CharSet=CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static string Protect(string secret)
    {
        if(string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("凭据不能为空");
        var bytes=Encoding.UTF8.GetBytes(secret);
        var ptr=Marshal.AllocHGlobal(bytes.Length);
        try {
            Marshal.Copy(bytes,0,ptr,bytes.Length);
            var input=new Blob{Length=bytes.Length,Data=ptr};
            if(!CryptProtectData(ref input,null,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out var output)) throw new InvalidOperationException("无法保存当前用户加密凭据");
            try {var encrypted=new byte[output.Length];Marshal.Copy(output.Data,encrypted,0,encrypted.Length);return Convert.ToBase64String(encrypted);}
            finally {LocalFree(output.Data);}
        } finally {CryptographicOperations.ZeroMemory(bytes);for(int i=0;i<bytes.Length;i++)Marshal.WriteByte(ptr,i,0);Marshal.FreeHGlobal(ptr);}
    }
    public static string Save(string path,string secret)
    {
        string encrypted=Protect(secret);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,encrypted);File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        return path;
    }
    public static string SaveProvider(string codexHome,string providerId,string key)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(providerId,"^[A-Za-z0-9_-]{1,64}$"))throw new ArgumentException("供应商 ID 仅允许字母、数字、下划线和连字符");
        return Save(Path.Combine(codexHome,"secrets","control-center-"+providerId+".dpapi"),key);
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace Link {
// Kernel-owned child lifetime: a service crash must not leave the tunnel running.
internal sealed class ProcessJob : IDisposable {
 IntPtr handle;
 [StructLayout(LayoutKind.Sequential)]struct Basic {public long ProcessTime,JobTime;public uint Flags;public UIntPtr Minimum,Maximum;public uint Active;public UIntPtr Affinity;public uint Priority,Scheduling;}
 [StructLayout(LayoutKind.Sequential)]struct IO {public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
 [StructLayout(LayoutKind.Sequential)]struct Extended {public Basic Limits;public IO Counters;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateJobObject(IntPtr attributes,string name);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetInformationJobObject(IntPtr job,int information,ref Extended data,int size);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 internal ProcessJob(){handle=CreateJobObject(IntPtr.Zero,null);var limits=new Extended{Limits=new Basic{Flags=0x2000}};if(handle==IntPtr.Zero||!SetInformationJobObject(handle,9,ref limits,Marshal.SizeOf(limits)))throw new Win32Exception();}
 internal void Add(Process process){if(!AssignProcessToJobObject(handle,process.Handle)){try{process.Kill();}catch{}throw new Win32Exception();}}
 public void Dispose(){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
}
}

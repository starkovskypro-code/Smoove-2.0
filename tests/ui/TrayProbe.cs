using System;using System.Runtime.InteropServices;using System.Text;
public static class SmooveTrayProbe {
 public delegate bool Callback(IntPtr h,IntPtr p);
 [DllImport("user32.dll")]public static extern bool EnumWindows(Callback c,IntPtr p);
 [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")]public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 public static void Find(uint pid){EnumWindows((h,p)=>{uint id;GetWindowThreadProcessId(h,out id);if(id==pid){var s=new StringBuilder(256);GetClassName(h,s,256);Console.WriteLine(h+" "+s); }return true;},IntPtr.Zero);}
}

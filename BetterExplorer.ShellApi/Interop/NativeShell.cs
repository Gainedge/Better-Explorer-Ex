using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Xml.Linq;
using BExplorer.Shell;
using BExplorer.Shell.Interop;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace BetterExplorer.ShellApi.Interop;

/// <summary>
/// Centralised shell P/Invoke declarations and low-level helper methods.
/// All COM calls and GDI operations originate here; the control layer only
/// calls the high-level public helpers.
/// </summary>
public static class NativeShell {
  // ── Known-folder GUIDs ────────────────────────────────────────────────────

  public static readonly Guid FOLDERID_QuickAccess = new("679f85cb-0220-4080-b29b-5540cc05aab6");
  public static readonly Guid FOLDERID_ComputerFolder = new("20D04FE0-3AEA-1069-A2D8-08002B30309D");
  public static readonly Guid FOLDERID_NetworkFolder = new("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53");
  public static readonly Guid FOLDERID_Libraries = new("1B3EA5DC-B587-4786-B4EF-BD1DC332AEAE");

  // Shell namespace CLSID for the Linux (WSL) virtual folder shown in Explorer's nav pane.
  public static readonly Guid CLSID_LinuxFolder = new("B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6");

  // Shell namespace for UPnP/WSD network devices (Media devices, Printers, Infrastructure, …).
  // This is the virtual folder Windows Explorer merges with FOLDERID_NetworkFolder to produce
  // the full "Network" tree including all device categories.
  private static readonly Guid CLSID_NetworkDevicesFolder =
      new("F02AA1B4-0C5E-45E3-B338-0E8C1A2F1E68");

  private static readonly Guid FOLDERID_Links =
      new("bfb9d5e0-c6a9-404c-b2b2-ae6db6af4968");

  // ── Global shell-call concurrency cap ────────────────────────────────────
  // Every blocking COM call into IShellItemImageFactory.GetImage() and
  // SHGetFileInfo() can take 100ms–1000ms (e.g. ResizeToFit on slow drives).
  // Without a cap, N rapid navigations leave N×8 thread-pool threads blocked
  // in COM, starving the pool and causing the progressive-slowdown pattern.
  // 8 slots matches ThumbConcurrency; stale workers wait here instead of
  // monopolising thread-pool threads.
  private static readonly SemaphoreSlim _shellCallSem = new(8, 8);

  // ── SIIGBF flags ──────────────────────────────────────────────────────────

  [Flags]
  public enum SIIGBF : int {
    ResizeToFit = 0x00,
    InCacheOnly = 0x10,
    IconOnly = 0x04,
    ThumbnailOnly = 0x08,
    CacheOnly_Thumb = InCacheOnly | ThumbnailOnly,
  }

  // ── COM interfaces ────────────────────────────────────────────────────────

  [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellItemImageFactory {
    [PreserveSig]
    int GetImage([In] SIZE size, [In] SIIGBF flags, out IntPtr phbm);
  }

  [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellFolder {
    void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
        out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    [PreserveSig]
    int EnumObjects(IntPtr hwnd, uint grfFlags, out IEnumIDList? ppenumIDList);
    void BindToObject(IntPtr pidl, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    void BindToStorage(IntPtr pidl, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out object ppv);
    [PreserveSig]
    int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    void CreateViewObject(IntPtr hwndOwner,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    void GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        ref uint rgfInOut);
    void GetUIObjectOf(IntPtr hwndOwner, uint cidl,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr rgfReserved,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig]
    int GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
    void SetNameOf(IntPtr hwnd, IntPtr pidl,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        uint uFlags, out IntPtr ppidlOut);
  }

  [ComImport, Guid("000214F2-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IEnumIDList {
    [PreserveSig] int Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumIDList ppenum);
  }

  [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellItem {
    void BindToHandler(IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(uint sigdnName,
        [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
  }

  [ComImport, Guid("8BE2D872-86AA-4D47-B776-32CCA40C7018"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IKnownFolderManager {
    void FolderIdFromCsidl(int nCsidl, out Guid pfid);
    void FolderIdToCsidl([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, out int pnCsidl);
    void GetFolderIds(out IntPtr ppKFId, out uint pCount);
    [PreserveSig]
    int GetFolder([MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
        [MarshalAs(UnmanagedType.Interface)] out IKnownFolder ppkf);
    void GetFolderByName([MarshalAs(UnmanagedType.LPWStr)] string pszCanonicalName,
        [MarshalAs(UnmanagedType.Interface)] out IKnownFolder ppkf);
    void RegisterFolder(IntPtr pKFD, out Guid pKFID);
    void UnregisterFolder([MarshalAs(UnmanagedType.LPStruct)] Guid rfid);
    void FindFolderFromPath([MarshalAs(UnmanagedType.LPWStr)] string pszPath, int mode,
        [MarshalAs(UnmanagedType.Interface)] out IKnownFolder ppkf);
    void FindFolderFromIDList(IntPtr pidl,
        [MarshalAs(UnmanagedType.Interface)] out IKnownFolder ppkf);
    void Redirect([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, IntPtr hwnd, uint flags,
        [MarshalAs(UnmanagedType.LPWStr)] string pszTargetPath, uint cFolders,
        IntPtr pExclusion, [MarshalAs(UnmanagedType.LPWStr)] out string ppszError);
  }

  [ComImport, Guid("3AA7AF7E-9B36-420C-A8E3-F77D4674A488"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IKnownFolder {
    void GetId(out Guid pkfid);
    void GetCategory(out uint pCategory);
    [PreserveSig]
    int GetShellItem(uint dwFlags,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    void GetPath(uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] out string ppszPath);
    void SetPath(uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string pszPath);
    void GetIDList(uint dwFlags, out IntPtr ppidl);
    void GetFolderType(out Guid pftid);
    void GetRedirectionCapabilities(out uint pCapabilities);
    void GetFolderDefinition(out IntPtr pKFD);
  }

  [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellLinkW {
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
        int cchMaxPath, IntPtr pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
        int cchIconPath, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
  }

  [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IPersistFile {
    void GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
  }

  // ── IFileOperation (shell copy/move/delete with Explorer UI) ─────────────

  /// <summary>
  /// The shell item interface used as source/dest for IFileOperation.
  /// Distinct from the private IShellItem above; this one is public for use
  /// in the progress sink and the file-operation helper.
  /// </summary>
  [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IShellItemOp {
    void BindToHandler(IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    void GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItemOp ppsi);
    void GetDisplayName(uint sigdnName,
        [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare([MarshalAs(UnmanagedType.Interface)] IShellItemOp psi, uint hint, out int piOrder);
  }

  [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IFileOperationProgressSink {
    void StartOperations();
    void FinishOperations(int hrResult);
    void PreRenameItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    void PostRenameItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
        int hrRename,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiNewlyCreated);
    void PreMoveItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    void PostMoveItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
        int hrMove,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiNewlyCreated);
    void PreCopyItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    void PostCopyItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
        int hrCopy,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiNewlyCreated);
    void PreDeleteItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem);
    void PostDeleteItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        int hrDelete,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiNewlyCreated);
    void PreNewItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    void PostNewItem(uint dwFlags,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
        [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName,
        uint dwFileAttributes,
        int hrNew,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiNewItem);
    void UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
    void ResetTimer();
    void PauseTimer();
    void ResumeTimer();
  }

  [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IFileOperation {
    [PreserveSig]
    int Advise(
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink pfops,
        out uint pdwCookie);
    [PreserveSig] int Unadvise(uint dwCookie);
    [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
    [PreserveSig]
    int SetProgressMessage(
        [MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
    [PreserveSig]
    int SetProgressDialog(
        [MarshalAs(UnmanagedType.Interface)] object popd);
    [PreserveSig]
    int SetProperties(
        [MarshalAs(UnmanagedType.Interface)] object pproparray);
    [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
    [PreserveSig]
    int ApplyPropertiesToItem(
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem);
    [PreserveSig]
    int ApplyPropertiesToItems(
        [MarshalAs(UnmanagedType.Interface)] object punkItems);
    [PreserveSig]
    int RenameItem(
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
    [PreserveSig]
    int RenameItems(
        [MarshalAs(UnmanagedType.Interface)] object pUnkItems,
        [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
    [PreserveSig]
    int MoveItem(
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
    [PreserveSig]
    int MoveItems(
        [MarshalAs(UnmanagedType.Interface)] object punkItems,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder);
    [PreserveSig]
    int CopyItem(
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
    [PreserveSig]
    int CopyItems(
        [MarshalAs(UnmanagedType.Interface)] object punkItems,
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder);
    [PreserveSig]
    int DeleteItem(
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiItem,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
    [PreserveSig]
    int DeleteItems(
        [MarshalAs(UnmanagedType.Interface)] object punkItems);
    [PreserveSig]
    int NewItem(
        [MarshalAs(UnmanagedType.Interface)] IShellItemOp psiDestinationFolder,
        uint dwFileAttributes,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName,
        [MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink? pfopsItem);
    [PreserveSig] int PerformOperations();
    [PreserveSig] int GetAnyOperationsAborted(out bool pfAnyOperationsAborted);
  }

  // ── IImageList (system image list — used to extract overlay icons) ─────────

  [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IImageList {
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
    [PreserveSig] int Draw(IntPtr pimldp);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, uint flags, out IntPtr picon);
    [PreserveSig] int GetImageInfo(int i, IntPtr pImageInfo);
    [PreserveSig] int Copy(int iDst, IntPtr punkSrc, int iSrc, uint uFlags);
    [PreserveSig] int Merge(int i1, IntPtr punk2, int i2, int dx, int dy, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int Clone(ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetImageRect(int i, IntPtr prc);
    [PreserveSig] int GetIconSize(out int cx, out int cy);
    [PreserveSig] int SetIconSize(int cx, int cy);
    [PreserveSig] int GetImageCount(out int pi);
    [PreserveSig] int SetImageCount(uint uNewCount);
    [PreserveSig] int SetBkColor(int clrBk, out int pclr);
    [PreserveSig] int GetBkColor(out int pclr);
    [PreserveSig] int BeginDrag(int iTrack, int dxHotspot, int dyHotspot);
    [PreserveSig] int EndDrag();
    [PreserveSig] int DragEnter(IntPtr hwndLock, int x, int y);
    [PreserveSig] int DragLeave(IntPtr hwndLock);
    [PreserveSig] int DragMove(int x, int y);
    [PreserveSig] int SetDragCursorImage(IntPtr punk, int iDrag, int dxHotspot, int dyHotspot);
    [PreserveSig] int DragShowNolock(bool fShow);
    [PreserveSig] int GetDragImage(IntPtr ppt, IntPtr pptHotspot, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetItemFlags(int i, out uint dwFlags);
    [PreserveSig] int GetOverlayImage(int iOverlay, out int piIndex);
  }

  // ── Structs ───────────────────────────────────────────────────────────────

  [StructLayout(LayoutKind.Sequential)]
  private struct SIZE { public int cx, cy; }

  [StructLayout(LayoutKind.Sequential)]
  private struct BITMAPINFOHEADER {
    public int biSize, biWidth, biHeight;
    public short biPlanes, biBitCount;
    public int biCompression, biSizeImage;
    public int biXPelsPerMeter, biYPelsPerMeter;
    public int biClrUsed, biClrImportant;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
  private struct WIN32_FIND_DATA {
    public uint dwFileAttributes;
    public long ftCreationTime;
    public long ftLastAccessTime;
    public long ftLastWriteTime;
    public uint nFileSizeHigh;
    public uint nFileSizeLow;
    public uint dwReserved0;
    public uint dwReserved1;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string cFileName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
    public string cAlternateFileName;
  }

  // STRRET is a C union: sizeof = 4 (uType) + pad + max(sizeof(LPWSTR), sizeof(UINT), MAX_PATH).
  // Use explicit layout so all three union members share the same offset.
  // NOTE: byte[] cStr and uint uOffset cannot coexist with IntPtr pOleStr in an explicit-layout
  // union under .NET Core (managed array = GC object field cannot overlap a non-object field).
  // Since all callers pass the struct directly to StrRetToBufW (which handles WSTR/OFFSET/CSTR
  // internally), we only need pOleStr accessible from C#.
  // Size = 8 (uType + pad) + 260 (MAX_PATH for cStr, the largest union member) = 268, rounded to 272.
  [StructLayout(LayoutKind.Explicit, Size = 272)]
  private struct STRRET {
    [FieldOffset(0)] public uint   uType;     // STRRET_WSTR=0, STRRET_OFFSET=1, STRRET_CSTR=2
    [FieldOffset(8)] public IntPtr pOleStr;   // STRRET_WSTR: CoTaskMem LPWSTR (also covers uOffset/cStr via StrRetToBufW)
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
  private struct SHFILEINFO {
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
  }

  // ── IID / CLSID constants ─────────────────────────────────────────────────

  private static readonly Guid IID_IShellItemImageFactory = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");
  private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
  private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
  private static readonly Guid IID_IShellItem2 = new("7E9FB0D3-919F-4307-AB2E-9B1860310C93");
  private static readonly Guid IID_IKnownFolderManager = new("8BE2D872-86AA-4D47-B776-32CCA40C7018");
  private static readonly Guid CLSID_KnownFolderManager = new("4DF0C730-DF9D-4AE3-9153-AA6B82E9795A");
  private static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
  private static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
  private static readonly Guid BHID_SFObject = new("3981e224-f559-11d3-8e3a-00c04f6837d5");

  // IFileOperation GUIDs
  private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
  private static readonly Guid IID_IFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
  private static readonly Guid IID_IShellItemOp = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

  // ISearchFolderItemFactory GUIDs (Windows Search virtual-folder search)
  private static readonly Guid CLSID_SearchFolderItemFactory  = new("14010E02-BBBD-41F0-88E3-EDA371216584");
  private static readonly Guid IID_ISearchFolderItemFactory   = new("A0FFBC28-5482-4366-BE27-3E81E78E06C2");
  private static readonly Guid IID_IShellItemArray            = new("B63EA76D-1F85-456F-A19C-48159EFA858B");
  // IConditionFactory2 / ICondition (structuredquery)
  private static readonly Guid CLSID_ConditionFactory         = new("E03E85B0-7BE3-4000-BA98-6C13DE9FA486");
  private static readonly Guid IID_IConditionFactory2         = new("71D222E1-432F-429e-8C13-B6DAFDE5077A");
  private static readonly Guid IID_ICondition                 = new("0FC988D4-C935-4b97-A973-46282EA175C8");
  // Folder type for generic search results (ShlGuid.h FOLDERTYPEID_GenericSearchResults)
  private static readonly Guid FOLDERTYPEID_GenericSearchResults = new("7fde1a1e-8b31-49a5-93b8-6be14cfa4943");

  // IFileOperation flags
  private const uint FOF_NOCONFIRMMKDIR   = 0x0200;
  private const uint FOF_NOCONFIRMATION   = 0x0010;  // suppress "are you sure?" prompts
  private const uint FOF_RENAMEONCOLLISION = 0x0008;
  private const uint FOF_SILENT           = 0x0004;  // suppress progress dialog
  private const uint FOF_NO_UI            = 0x0614;  // FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR
  private const uint FOFX_ADDUNDORECORD   = 0x20000000;  // add to Explorer undo stack

  // ── IShellFolder constants ────────────────────────────────────────────────

  private const uint SHCONTF_FOLDERS        = 0x0020;
  private const uint SHCONTF_NONFOLDERS     = 0x0040;
  private const uint SHCONTF_NETPRINTERSRCH = 0x0200;  // include network printers
  private const uint SHCONTF_SHAREABLE      = 0x0400;  // include shareable resources
  private const uint SHCONTF_FASTITEMS      = 0x2000;
  private const uint SHCONTF_ENABLE_ASYNC   = 0x8000;  // return partial list now; more via SHCNE_UPDATEDIR
  private const uint SFGAO_FILESYSTEM = 0x40000000;
  private const uint SFGAO_FOLDER = 0x20000000;
  private const uint SHGDN_FORPARSING = 0x8000;
  private const int  CSIDL_NETWORK = 0x0012;

  // ── SHGetFileInfo constants ───────────────────────────────────────────────

  private const uint SHGFI_ICON           = 0x100;
  private const uint SHGFI_LARGEICON      = 0x0;
  private const uint SHGFI_SMALLICON      = 0x1;
  private const uint SHGFI_SYSICONINDEX   = 0x4000;
  private const uint SHGFI_OVERLAYINDEX   = 0x40;
  private const uint SHGFI_PIDL           = 0x8;    // pszPath is an ITEMIDLIST (PIDL)
  private const uint SHGFI_USEFILEATTRIBUTES = 0x10;   // don't touch file; use dwFileAttributes
  private const uint SHGFI_TYPENAME       = 0x400;  // fill szTypeName with friendly type string
  private const uint DI_NORMAL = 0x3;

  // System image list size identifiers for SHGetImageList.
  private const int SHIL_LARGE = 0;   // 32×32
  private const int SHIL_SMALL = 1;   // 16×16

  // IID for IImageList — used by SHGetImageList.
  private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");


  // Per-overlay-slot pixel cache (overlay slot 1-15 → premultiplied BGRA bytes + dimensions).
  // A null Pixels value means "slot exists but icon could not be rendered".
  private static readonly Dictionary<int, (byte[]? Pixels, int W, int H)> _overlayPixelCache = new();
  private static readonly object _overlayPixelCacheLock = new();
  // Tracks which slots are currently being fetched; other callers wait in the lock.
  private static readonly HashSet<int> _overlayPixelInFlight = new();

  // ── FindFirstFileEx constants ─────────────────────────────────────────────

  private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
  private const uint FILE_ATTRIBUTE_HIDDEN = 0x02;
  private const uint FILE_ATTRIBUTE_REPARSE = 0x0400;
  // OneDrive / cloud files that are not locally available carry these attribute bits.
  private const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;
  private const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
  private const uint FILE_ATTRIBUTE_PINNED = 0x00080000;
  private const int FINDEX_INFO_BASIC = 1;
  private const int FINDEX_SEARCH_NAME = 0;
  private const uint LARGE_FETCH = 2;
  private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

  // ── Shell property keys (PKEY) ────────────────────────────────────────────
  // PKEY_Size         = {B725F130-47EF-101A-A5F1-02608C9EEBAC}, pid 12
  // PKEY_DateModified = {B725F130-47EF-101A-A5F1-02608C9EEBAC}, pid 14
  // PKEY_ItemTypeText = {B725F130-47EF-101A-A5F1-02608C9EEBAC}, pid 4  ("File folder", "PNG File", …)
  // PKEY_FileAttributes = {B725F130-47EF-101A-A5F1-02608C9EEBAC}, pid 13
  // IShellItem2 methods take BExplorer.Shell.Interop.PROPERTYKEY (int pid).
  private static readonly Guid _pkeyStorageFmtId = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");
  // The outer public PROPERTYKEY (int pid) is used here; qualified to avoid
  // shadowing by the private inner struct (uint pid).
  private static global::BetterExplorer.ShellApi.Interop.PROPERTYKEY PKEY_Size         => new() { fmtid = _pkeyStorageFmtId, pid = 12 };
  private static global::BetterExplorer.ShellApi.Interop.PROPERTYKEY PKEY_DateModified => new() { fmtid = _pkeyStorageFmtId, pid = 14 };
  private static global::BetterExplorer.ShellApi.Interop.PROPERTYKEY PKEY_ItemTypeText => new() { fmtid = _pkeyStorageFmtId, pid =  4 };
  private static global::BetterExplorer.ShellApi.Interop.PROPERTYKEY PKEY_FileAttribs  => new() { fmtid = _pkeyStorageFmtId, pid = 13 };
  // System.Network.DeviceType — {A3B29791-7713-4E1D-BB40-17DB85F01831} pid 100
  // Explorer uses this uint property to group items in the Network folder:
  //   1 = Computer  2 = Printer  3 = Media device  4 = Infrastructure  5 = Storage
  private static readonly Guid _pkeyNetworkFmtId = new("A3B29791-7713-4E1D-BB40-17DB85F01831");
  private static global::BetterExplorer.ShellApi.Interop.PROPERTYKEY PKEY_Network_DeviceType =>
      new() { fmtid = _pkeyNetworkFmtId, pid = 100 };

  private static string NetworkDeviceTypeToCategory(uint v) => v switch {
    1 => "Computers",
    2 => "Printers",
    3 => "Media devices",
    4 => "Infrastructure",
    5 => "Storage",
    _ => "Other devices",
  };

  // Maps the shell type-name string returned by SHGetFileInfo(SHGFI_TYPENAME) or
  // PKEY_Device_CategoryGroup to the Explorer-style group header labels.
  // Strings must stay in sync with:
  //   ShellTreeView.xaml.cs  — NetworkCategoryOrder()
  //   ShellListView.xaml.cs  — _networkTypeGroupOrder
  public static string NormalizeNetworkCategory(string raw) {
    if (string.IsNullOrWhiteSpace(raw)) return "Other devices";
    var s = raw.Trim();

    // Already-normalised labels pass through unchanged.
    if (s is "Media devices" or "Computers" or "Storage" or "Printers"
           or "Infrastructure" or "Other devices" or "Network")
      return s;

    // SHGetFileInfo(SHGFI_TYPENAME) returns these exact strings from the shell namespace.
    // "Computer" → individual machine node  →  Computers
    if (s.Equals("Computer", StringComparison.OrdinalIgnoreCase))
      return "Computers";

    // Workgroup / domain containers are network organisational nodes, not individual
    // computers.  Explorer groups them separately when there are sub-workgroups visible.
    if (s.Contains("Workgroup",       StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Windows Network", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Network",         StringComparison.OrdinalIgnoreCase))
      return "Network";

    if (s.Contains("Printer", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Print",   StringComparison.OrdinalIgnoreCase))
      return "Printers";

    if (s.Contains("Media",    StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Renderer", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Player",   StringComparison.OrdinalIgnoreCase))
      return "Media devices";

    if (s.Contains("Infrastr", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Router",   StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Gateway",  StringComparison.OrdinalIgnoreCase) ||
        s.Contains("Switch",   StringComparison.OrdinalIgnoreCase))
      return "Infrastructure";

    if (s.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
        s.Contains("NAS",     StringComparison.OrdinalIgnoreCase))
      return "Storage";

    return "Other devices";
  }

  // Returns the Explorer-style network group label for a shell item.
  // Strategy:
  //   1. SHGetPropertyStoreFromParsingName + PKEY_Network_DeviceType (uint).
  //      Works for items whose property store is immediately available.
  //   2. IShellItem2::GetPropertyStore(BestEffort) via SHCreateItemWithParent.
  //      Used when GPS_DEFAULT returns VT_EMPTY (e.g. WSD/PnpX devices).
  //   3. UPnP/SSDP path heuristic: the "uuid:upnp-<DeviceType>-..." segment that
  //      appears in virtual SSDP paths encodes the UPnP device type directly.
  //   4. Structural fallback for classic WNet nodes:
  //      UNC "\\name" folders = Computers, other virtual folders = Network,
  //      non-folder items (NETPRINTERSRCH) = Printers.
  private static string GetNetworkItemCategory(
      IShellFolder parentFolder, IntPtr childPidl,
      string parsePath, bool isFolder) {

    // Step 1 — property store via parsing name.
    try {
      if (SHGetPropertyStoreFromParsingName("shell:" + parsePath, IntPtr.Zero, GPS_DEFAULT,
              typeof(IPropertyStore).GUID, out var store) == 0 && store != null) {
        try {
          using var pv = new PropVariant();
          var pk = PKEY_Network_DeviceType;
          if (store.GetValue(ref pk, pv) == HResult.S_OK && pv.Value is uint devType)
            return NetworkDeviceTypeToCategory(devType);
        } finally { Marshal.ReleaseComObject(store); }
      }
    } catch { }

    // Step 2 — IShellItem2 via SHCreateItemWithParent + GPS_BESTEFFORT.
    // This succeeds for WSD/PnpX devices whose slow property handler has
    // the device type but is not returned by GPS_DEFAULT above.
    try {
      SHCreateItemWithParent(IntPtr.Zero, parentFolder, childPidl,
          typeof(IShellItem2).GUID, out var si2);
      if (si2 != null) {
        try {
          var iid = typeof(IPropertyStore).GUID;
          if (si2.GetPropertyStore(GetPropertyStoreOptions.BestEffort, ref iid, out var store2) == 0
              && store2 != null) {
            try {
              using var pv2 = new PropVariant();
              var pk2 = PKEY_Network_DeviceType;
              if (store2.GetValue(ref pk2, pv2) == HResult.S_OK && pv2.Value is uint devType2)
                return NetworkDeviceTypeToCategory(devType2);
            } finally { Marshal.ReleaseComObject(store2); }
          }
          // Last resort: read the item-type text and look for "printer" keyword.
          var pkType = PKEY_ItemTypeText;
          if (si2.GetString(ref pkType, out var typeText) == HResult.S_OK
              && !string.IsNullOrEmpty(typeText)
              && typeText.IndexOf("print", StringComparison.OrdinalIgnoreCase) >= 0)
            return "Printers";
        } finally { Marshal.ReleaseComObject(si2); }
      }
    } catch { }

    // Step 3 — UPnP device-type encoded in the parsing path.
    // SSDP virtual items have a segment like "uuid:upnp-<DeviceType>-<uuid>".
    // The UPnP device type strings are defined by the UPnP Forum device schema.
    var upnpCategory = GetCategoryFromUpnpPath(parsePath);
    if (upnpCategory != null) return upnpCategory;

    // Step 4 — structural fallback for classic WNet nodes.
    if (isFolder)
      return parsePath.StartsWith(@"\\", StringComparison.Ordinal) ? "Computers" : "Network";
    return "Printers";
  }

  // Extracts the Explorer network category from a UPnP SSDP parsing path that contains
  // a "uuid:upnp-<DeviceType>-<guid>" segment, e.g.:
  //   ::{...}\Provider\Microsoft.Networking.SSDP//uuid:upnp-InternetGatewayDevice-...
  // Returns null for paths that are not from a recognised UPnP/WSD provider.
  private static string? GetCategoryFromUpnpPath(string parsePath) {
    const string prefix = "uuid:upnp-";
    int idx = parsePath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
    if (idx < 0) {
      // WSD (PnpX) devices: property store (step 2) already ran and failed to
      // return a device type.  Most WSD devices without a resolvable type are
      // computers; return "Computers" as the best guess.
      if (parsePath.IndexOf("Microsoft.Networking.PnpX", StringComparison.OrdinalIgnoreCase) >= 0)
        return "Computers";
      if (parsePath.IndexOf("Microsoft.Networking.WSD", StringComparison.OrdinalIgnoreCase) >= 0)
        return "Printers";
      // Any other SSDP path without a recognised uuid:upnp- device type is an
      // unknown device — avoid the WNet structural fallback (which would wrongly
      // classify it as a computer or printer).
      if (parsePath.IndexOf("Microsoft.Networking.SSDP", StringComparison.OrdinalIgnoreCase) >= 0)
        return "Other devices";
      return null;
    }

    // Extract the device type token between the prefix and the next '-' (the uuid separator).
    int start = idx + prefix.Length;
    int end   = parsePath.IndexOf('-', start);
    string deviceType = end >= 0
        ? parsePath.Substring(start, end - start)
        : parsePath.Substring(start);

    // Map UPnP Forum device type strings to Explorer group labels.
    return deviceType.ToUpperInvariant() switch {
      // Media
      "MEDIARENDERER" or "MEDIASERVER" or "DIGITALMEDIARENDERER"
        or "DIGITALMEDIASERVER" or "DIGITALMEDIAPLAYER"
        => "Media devices",

      // Printers
      "PRINTDEVICE" or "PRINTER"
        => "Printers",

      // Network infrastructure (routers, gateways, access points)
      "INTERNETGATEWAYDEVICE" or "WANDEVICE" or "WANCONNECTIONDEVICE"
        or "ROUTER" or "WIRELESSACCESSPOINT" or "WIFIAP"
        => "Infrastructure",

      // Storage
      "NETWORKATTACHEDSTORAGE" or "STORAGEDRIVE"
        => "Storage",

      // Everything else is Other devices
      _ => "Other devices",
    };
  }

  // ── P/Invoke declarations ─────────────────────────────────────────────────

  [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
  private static extern int SHGetPropertyStoreFromParsingName(
      string pszPath, IntPtr pbc, int flags,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

  // GPS_DEFAULT — read-only, all properties, no slow/offline items.
  private const int GPS_DEFAULT = 0;

  [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
  private static extern void SHCreateItemFromParsingName(
      string pszPath, IntPtr pbc,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

  [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName",
             CharSet = CharSet.Unicode, PreserveSig = false)]
  private static extern void SHCreateItemFromParsingNameOp(
      string pszPath, IntPtr pbc,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItemOp ppv);

  [DllImport("shell32.dll")]
  private static extern int SHGetKnownFolderIDList(
      [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
      uint dwFlags, IntPtr hToken, out IntPtr ppidl);

  [DllImport("shell32.dll", PreserveSig = false)]
  private static extern void SHCreateItemFromIDList(
      IntPtr pidl,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

  [DllImport("shell32.dll", PreserveSig = false)]
  private static extern void SHGetKnownFolderItem(
      [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
      uint dwFlags, IntPtr hToken,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

  // Returns a private IShellItem — used for shell-namespace parent resolution.
  [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName",
             CharSet = CharSet.Unicode, PreserveSig = false)]
  private static extern void SHCreateItemFromParsingNameShell(
      string pszPath, IntPtr pbc,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

  // Returns IShellItem2 — used for property-store queries (size, dates, type text).
  [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName",
             CharSet = CharSet.Unicode, PreserveSig = false)]
  private static extern void SHCreateItemFromParsingNameItem2(
      string pszPath, IntPtr pbc,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItem2 ppv);

  // Creates an IShellItem2 from a parent IShellFolder + child PIDL — used in network enumeration.
  [DllImport("shell32.dll", PreserveSig = false)]
  private static extern void SHCreateItemWithParent(
      IntPtr pidlParent,
      [MarshalAs(UnmanagedType.Interface)] IShellFolder psfParent,
      IntPtr pidl,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItem2 ppv);

  // Converts any COM shell object to its absolute PIDL (needed for FindFolderFromIDList).
  [DllImport("shell32.dll", PreserveSig = false)]
  private static extern void SHGetIDListFromObject(
      [MarshalAs(UnmanagedType.IUnknown)] object punk,
      out IntPtr ppidl);

  [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetKnownFolderPath")]
  private static extern int SHGetKnownFolderPathNative(
      [MarshalAs(UnmanagedType.LPStruct)] Guid rfid,
      uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

  [DllImport("shell32.dll", CharSet = CharSet.Auto)]
  private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
      ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

  // PIDL overload — pszPath is treated as an ITEMIDLIST* when SHGFI_PIDL is set.
  [DllImport("shell32.dll", CharSet = CharSet.Auto)]
  private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes,
      ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

  [DllImport("shell32.dll", PreserveSig = true)]
  private static extern int SHGetImageList(int iImageList,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

  [DllImport("ole32.dll")]
  private static extern void CoTaskMemFree(IntPtr pv);

  [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
  private static extern int StrRetToBufW(ref STRRET pstr, IntPtr pidl, [Out] char[] pszBuf, uint cchBuf);

  // Resolves all three STRRET union cases (WSTR/OFFSET/CSTR) into a managed string.
  // Must be called before freeing childPidl.
  private static string? StrRetToStr(ref STRRET strret, IntPtr pidl) {
    const int MaxPath = 520;
    var buf = new char[MaxPath];
    return StrRetToBufW(ref strret, pidl, buf, (uint)buf.Length) == 0
        ? new string(buf).TrimEnd('\0')
        : null;
  }

  [DllImport("ole32.dll")]
  private static extern int CoCreateInstance(
      [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
      IntPtr pUnkOuter, uint dwClsContext,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out object ppv);

  // uxtheme ordinals — used to push dark-mode into the shell dialogs that
  // IFileOperation shows, which otherwise appear light in a WinUI 3 process.
  // SetPreferredAppMode = ordinal 135 (Win10 1903+)
  // FlushMenuThemes     = ordinal 136
  [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
  private static extern int SetPreferredAppMode(int mode); // 0=Default,1=AllowDark,2=ForceDark,3=ForceLight

  [DllImport("uxtheme.dll", EntryPoint = "#136", SetLastError = false)]
  private static extern void FlushMenuThemes();

  [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
  [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdi);
  [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
  [DllImport("gdi32.dll")]
  private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
      [Out] byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
  [DllImport("gdi32.dll")]
  private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi,
      uint usage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
  [DllImport("user32.dll")]
  private static extern bool DestroyIcon(IntPtr hIcon);
  [DllImport("user32.dll")]
  private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
      int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);
  [DllImport("user32.dll")]
  private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
  [DllImport("gdi32.dll")]
  private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

  [StructLayout(LayoutKind.Sequential)]
  private struct ICONINFO {
    public bool fIcon;
    public uint xHotspot;
    public uint yHotspot;
    public IntPtr hbmMask;
    public IntPtr hbmColor;
  }

  [StructLayout(LayoutKind.Sequential)]
  private struct BITMAP {
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public short bmPlanes;
    public short bmBitsPixel;
    public IntPtr bmBits;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
  private static extern uint GetFileAttributesW(string lpFileName);

  // ── Context-menu P/Invoke ─────────────────────────────────────────────────

  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  internal static extern IntPtr ILCreateFromPathW([MarshalAs(UnmanagedType.LPWStr)] string pszPath);

  [DllImport("shell32.dll")]
  internal static extern void ILFree(IntPtr pidl);

  [DllImport("shell32.dll", PreserveSig = true)]
  internal static extern int SHCreateItemArrayFromIDLists(
      uint cidl,
      [In] IntPtr[] rgpidl,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out IShellItemArrayCM ppsiItemArray);

  [DllImport("shell32.dll", PreserveSig = true)]
  private static extern int SHCreateShellItemArrayFromShellItem(
      [MarshalAs(UnmanagedType.Interface)] IShellItem psi,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out object ppv);

  [DllImport("shell32.dll", PreserveSig = true)]
  internal static extern int SHBindToParent(
      IntPtr pidl,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out object ppv,
      out IntPtr ppidlLast);

  [DllImport("shell32.dll", PreserveSig = true)]
  internal static extern int SHGetDesktopFolder(
      [MarshalAs(UnmanagedType.Interface)] out object ppshf);

  [DllImport("shell32.dll", PreserveSig = true)]
  private static extern int SHGetSpecialFolderLocation(
      IntPtr hwndOwner, int nFolder, out IntPtr ppidl);

  [DllImport("shell32.dll", PreserveSig = true)]
  internal static extern int SHParseDisplayName(
      [MarshalAs(UnmanagedType.LPWStr)] string pszName,
      IntPtr pbc,
      out IntPtr ppidl,
      uint sfgaoIn,
      out uint psfgaoOut);

  // Binds a PIDL directly to an interface without needing the parent IShellFolder.
  // Passing null (IntPtr.Zero) for psfParent uses the desktop folder as root.
  [DllImport("shell32.dll", PreserveSig = true)]
  private static extern int SHBindToObject(
      IntPtr psfParent,
      IntPtr pidl,
      IntPtr pbc,
      [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
      [MarshalAs(UnmanagedType.Interface)] out object ppv);

  // ── Public IShellFolder (for context-menu use) ────────────────────────────

  [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IShellFolderCM {
    [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
        out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags,
        [MarshalAs(UnmanagedType.Interface)] out object ppenumIDList);
    [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    [PreserveSig] int CreateViewObject(IntPtr hwndOwner,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int GetAttributesOf(uint cidl,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        ref uint rgfInOut);
    [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl,
        [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr rgfReserved,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags,
        [MarshalAs(UnmanagedType.Interface)] out object pName);
    [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        uint uFlags, out IntPtr ppidlOut);
  }

  [DllImport("user32.dll")]
  internal static extern IntPtr CreatePopupMenu();

  [DllImport("user32.dll")]
  internal static extern bool DestroyMenu(IntPtr hMenu);

  [DllImport("user32.dll")]
  internal static extern int GetMenuItemCount(IntPtr hMenu);

  [StructLayout(LayoutKind.Sequential)]
  internal struct MSG {
    public IntPtr hwnd;
    public uint   message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint   time;
    public int    ptX, ptY;
  }

  [DllImport("user32.dll")]
  internal static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
      uint wMsgFilterMax, uint wRemoveMsg);

  [DllImport("user32.dll")]
  internal static extern bool TranslateMessage(ref MSG lpMsg);

  [DllImport("user32.dll")]
  internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);

  // Pump all pending messages on the current STA thread for up to <ms> milliseconds.
  // This lets async COM shell extensions post their completion callbacks.
  internal static void PumpMessagesFor(int ms) {
    var deadline = Environment.TickCount64 + ms;
    while (Environment.TickCount64 < deadline) {
      while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1 /*PM_REMOVE*/)) {
        TranslateMessage(ref msg);
        DispatchMessageW(ref msg);
      }
      Thread.Sleep(1);
    }
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  internal static extern bool GetMenuItemInfoW(IntPtr hMenu, uint item, bool fByPosition,
      ref MENUITEMINFOW lpmii);

  // ── IShellItemArray (context-menu variant) ────────────────────────────────

  [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IShellItemArrayCM {
    [PreserveSig] int BindToHandler(IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int GetPropertyStore(int flags,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [PreserveSig] int GetAttributes(uint AttribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex,
        [MarshalAs(UnmanagedType.Interface)] out object ppsi);
    [PreserveSig] int EnumItems(
        [MarshalAs(UnmanagedType.Interface)] out object ppenumShellItems);
  }


  
  // PROPERTYKEY: fmtid (GUID) + pid (DWORD) — must be sequential/no padding
  [StructLayout(LayoutKind.Sequential)]
  private struct PROPERTYKEY {
    public Guid fmtid;
    public uint pid;
  }

  // ── IContextMenu ──────────────────────────────────────────────────────────

  [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IContextMenuCM {
    [PreserveSig]
    int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig]
    int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig]
    int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
        IntPtr pszName, uint cchMax);
  }

  // ── IContextMenu2 (adds HandleMenuMsg) ───────────────────────────────────

  /// <summary>
  /// COM interface for IContextMenu2. The vtable must list IContextMenu methods first
  /// (in order), then the IContextMenu2 addition, to match the native COM vtable layout.
  /// </summary>
  [ComImport, Guid("000214F4-0000-0000-C000-000000000046"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IContextMenu2CM {
    // ── IContextMenu ──────────────────────────────────────────────────────
    [PreserveSig]
    int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig]
    int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig]
    int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
        IntPtr pszName, uint cchMax);
    // ── IContextMenu2 ─────────────────────────────────────────────────────
    [PreserveSig]
    int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
  }

  // ── IContextMenu3 (adds HandleMenuMsg2) ──────────────────────────────────

  [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"),
   InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  public interface IContextMenu3CM {
    // ── IContextMenu ──────────────────────────────────────────────────────
    [PreserveSig]
    int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig]
    int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
    [PreserveSig]
    int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved,
        IntPtr pszName, uint cchMax);
    // ── IContextMenu2 ─────────────────────────────────────────────────────
    [PreserveSig]
    int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    // ── IContextMenu3 ─────────────────────────────────────────────────────
    [PreserveSig]
    int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
  }

  // ── GetCommandString type codes (GCS_*) ───────────────────────────────────

  public const uint GCS_VERBA     = 0x00000000;  // ANSI verb string
  public const uint GCS_HELPTEXTA = 0x00000001;  // ANSI help text
  public const uint GCS_VALIDATEA = 0x00000002;  // validate ANSI verb
  public const uint GCS_VERBW     = 0x00000004;  // Unicode verb string
  public const uint GCS_HELPTEXTW = 0x00000005;  // Unicode help text
  public const uint GCS_VALIDATEW = 0x00000006;  // validate Unicode verb
  public const uint GCS_UNICODE   = 0x00000004;  // Unicode flag bit

  // ── MENUITEMINFOW (Unicode) ───────────────────────────────────────────────

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct MENUITEMINFOW {
    public uint cbSize;
    public uint fMask;
    public uint fType;
    public uint fState;
    public uint wID;
    public IntPtr hSubMenu;
    public IntPtr hbmpChecked;
    public IntPtr hbmpUnchecked;
    public UIntPtr dwItemData;
    public IntPtr dwTypeData;   // pointer to caller-allocated WCHAR buffer
    public uint cch;
    public IntPtr hbmpItem;

    // MIIM_* flags
    public const uint MIIM_STATE    = 0x00000001;
    public const uint MIIM_ID       = 0x00000002;
    public const uint MIIM_SUBMENU  = 0x00000004;
    public const uint MIIM_FTYPE    = 0x00000100;
    public const uint MIIM_STRING   = 0x00000040;
    public const uint MIIM_BITMAP   = 0x00000080;

    // Special HBMMENU values
    public static readonly IntPtr HBMMENU_CALLBACK = new(-1); // owner-draw icon

    // MFT_* flags
    public const uint MFT_SEPARATOR = 0x00000800;
    public const uint MFT_STRING    = 0x00000000;

    // MFS_* flags
    public const uint MFS_DISABLED  = 0x00000003;
    public const uint MFS_GRAYED    = 0x00000003;
  }

  // ── CMINVOKECOMMANDINFOEX ─────────────────────────────────────────────────

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
  public struct CMINVOKECOMMANDINFOEX {
    public int    cbSize;       // sizeof(CMINVOKECOMMANDINFOEX)
    public int    fMask;        // CMIC_MASK_* flags
    public IntPtr hwnd;
    public IntPtr lpVerb;       // verb or MAKEINTRESOURCE(offset)
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int    nShow;        // SW_SHOWNORMAL
    public int    dwHotKey;
    public IntPtr hIcon;
    // Ex fields
    [MarshalAs(UnmanagedType.LPStr)]
    public string? lpTitle;
    public IntPtr lpVerbW;      // Unicode verb
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpParametersW;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpDirectoryW;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpTitleW;
    public POINT  ptInvoke;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct POINT { public int X; public int Y; }

  // ── Cloud/offline detection ───────────────────────────────────────────────

  /// <summary>
  /// Returns <see langword="true"/> when the file is a cloud placeholder that has not
  /// been downloaded locally (OneDrive, SharePoint, etc.).
  /// These items must use <see cref="GetStorageThumbnailAsync"/> because
  /// <c>IShellItemImageFactory</c> returns <c>E_PENDING</c> for them until the shell
  /// has finished its own async generation, which can take several seconds.
  /// </summary>
  public static bool IsCloudOnlyItem(string path) {
    try {
      uint attrs = GetFileAttributesW(path);
      if (attrs == 0xFFFFFFFF)
        return false; // INVALID_FILE_ATTRIBUTES
                      // RECALL_ON_DATA_ACCESS: cloud-only (dehydrated).
                      // RECALL_ON_OPEN: also fetched remotely on access.
                      // Exclude PINNED — pinned items are locally available.
      const uint cloudBits = FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN;
      return (attrs & cloudBits) != 0 && (attrs & FILE_ATTRIBUTE_PINNED) == 0;
    } catch { return false; }
  }

  // ── Windows.Storage thumbnail pipeline (for cloud items) ─────────────────

  // Maps file extensions to the best ThumbnailMode for content-based thumbnails.
  private static ThumbnailMode ThumbnailModeForExt(string ext) => ext.ToLowerInvariant() switch {
    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".tif"
        or ".webp" or ".heic" or ".heif" or ".raw" or ".cr2" or ".nef"
        or ".arw" or ".dng" => ThumbnailMode.PicturesView,
    ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv"
        or ".m4v" or ".flv" or ".webm" => ThumbnailMode.VideosView,
    ".mp3" or ".flac" or ".aac" or ".ogg" or ".wma" => ThumbnailMode.MusicView,
    _ => ThumbnailMode.SingleItem,
  };

  /// <summary>
  /// Retrieves raw premultiplied BGRA pixel data for a thumbnail via the
  /// <c>Windows.Storage</c> API, keeping every await off the UI thread via
  /// <c>ConfigureAwait(false)</c>.  Returns <see langword="null"/> when the OS
  /// returns a generic type icon instead of real content (caller should retry).
  /// Call <see cref="PixelsToBitmapSync"/> on the UI thread to turn the result
  /// into a <see cref="WriteableBitmap"/>.
  /// </summary>
  public static async Task<(byte[]? Pixels, int W, int H)> GetStorageThumbnailPixelsAsync(
      string path, uint size, CancellationToken ct) {
    try {
      ct.ThrowIfCancellationRequested();

      var reqSize = (uint)Math.Max(size, 16);
      bool isFolder = (GetFileAttributesW(path) & FILE_ATTRIBUTE_DIRECTORY) != 0;
      var mode = isFolder
          ? ThumbnailMode.SingleItem
          : ThumbnailModeForExt(Path.GetExtension(path));

      StorageItemThumbnail? thumbnail;
      if (isFolder) {
        var folder = await StorageFolder.GetFolderFromPathAsync(path)
            .AsTask(ct).ConfigureAwait(false);
        thumbnail = await folder.GetThumbnailAsync(mode, reqSize,
            ThumbnailOptions.UseCurrentScale).AsTask(ct).ConfigureAwait(false);
      } else {
        var file = await StorageFile.GetFileFromPathAsync(path)
            .AsTask(ct).ConfigureAwait(false);
        thumbnail = await file.GetThumbnailAsync(mode, reqSize,
            ThumbnailOptions.UseCurrentScale).AsTask(ct).ConfigureAwait(false);
      }

      if (thumbnail == null || thumbnail.Size == 0)
        return (null, 0, 0);
      if (thumbnail.Type == ThumbnailType.Icon)
        return (null, 0, 0);

      ct.ThrowIfCancellationRequested();

      var decoder = await BitmapDecoder.CreateAsync(thumbnail)
          .AsTask(ct).ConfigureAwait(false);
      var softBitmap = await decoder.GetSoftwareBitmapAsync()
          .AsTask(ct).ConfigureAwait(false);
      ct.ThrowIfCancellationRequested();

      using var converted = SoftwareBitmap.Convert(
          softBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

      int w = converted.PixelWidth, h = converted.PixelHeight;
      var buf = new byte[w * h * 4];
      converted.CopyToBuffer(buf.AsBuffer());
      return (buf, w, h);
    } catch (OperationCanceledException) { return (null, 0, 0); } catch { return (null, 0, 0); }
  }

  /// <summary>
  /// Convenience overload: retrieves a thumbnail via <c>Windows.Storage</c> and
  /// materialises it as a <see cref="WriteableBitmap"/> on the calling thread.
  /// Must be called on the UI thread (for <see cref="PixelsToBitmapSync"/>).
  /// </summary>
  public static async Task<WriteableBitmap?> GetStorageThumbnailAsync(
      string path, uint size, CancellationToken ct) {
    var (pixels, w, h) = await GetStorageThumbnailPixelsAsync(path, size, ct)
        .ConfigureAwait(true);   // resume on UI thread for WriteableBitmap creation
    if (pixels == null)
      return null;
    return PixelsToBitmapSync(pixels, w, h);
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr FindFirstFileEx(
      string lpFileName, int fInfoLevelId, out WIN32_FIND_DATA lpFindFileData,
      int fSearchOp, IntPtr lpSearchFilter, uint dwAdditionalFlags);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
  private static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);
  [DllImport("kernel32.dll")]
  private static extern bool FindClose(IntPtr hFindFile);

  // ── Bitmap helpers ────────────────────────────────────────────────────────

  /// <summary>
  /// Returns an HBITMAP for a file-system or shell parsing path via IShellItemImageFactory.
  /// Returns IntPtr.Zero on failure. Caller is responsible for DeleteObject.
  /// </summary>
  /// <summary>COM S_OK == 0; E_PENDING means the shell is generating the thumbnail asynchronously.</summary>
  public const int E_PENDING = unchecked((int)0x8000000A);

  public static IntPtr TryGetShellHBitmap(string path, uint size, SIIGBF flags) {
    TryGetShellHBitmapHr(path, size, flags, out var hbm);
    return hbm;
  }

  /// <summary>
  /// Like <see cref="TryGetShellHBitmap"/> but also returns the raw HRESULT so callers
  /// can distinguish a permanent failure from <c>E_PENDING</c> (cloud thumbnail not ready).
  /// </summary>
  public static int TryGetShellHBitmapHr(string path, uint size, SIIGBF flags, out IntPtr hbm) {
    hbm = IntPtr.Zero;
    try {
      SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out var factory);
      int hr = factory.GetImage(new SIZE { cx = (int)size, cy = (int)size }, flags, out hbm);
      Marshal.ReleaseComObject(factory);
      if (hr != 0)
        hbm = IntPtr.Zero;
      return hr;
    } catch { return unchecked((int)0x80004005); } // E_FAIL
  }

  /// <summary>
  /// Returns an HBITMAP for a known virtual folder via SHGetKnownFolderIDList → SHCreateItemFromIDList.
  /// Returns IntPtr.Zero on failure. Caller is responsible for DeleteObject.
  /// </summary>
  public static IntPtr TryGetIDListKnownFolderHBitmap(Guid folderId, uint size) {
    try {
      int hr = SHGetKnownFolderIDList(folderId, 0, IntPtr.Zero, out var pidl);
      if (hr != 0 || pidl == IntPtr.Zero) {
        var path = "shell:::" + folderId.ToString("B");
        var result = IntPtr.Zero;
        hr = TryGetShellHBitmapHr(path, size, SIIGBF.IconOnly, out result);
        if (hr != 0)
          return IntPtr.Zero;
        return result;
      }
      
      try {
        SHCreateItemFromIDList(pidl, IID_IShellItemImageFactory, out var factory);
        int hr2 = factory.GetImage(new SIZE { cx = (int)size, cy = (int)size }, SIIGBF.IconOnly, out var hbm);
        Marshal.ReleaseComObject(factory);
        return hr2 == 0 ? hbm : IntPtr.Zero;
      } finally { CoTaskMemFree(pidl); }
    } catch { return IntPtr.Zero; }
  }

  /// <summary>
  /// Copies HBITMAP pixel data into a premultiplied BGRA byte array (top-down).
  /// Returns (null,0,0) on failure.
  /// </summary>
  public static (byte[]? pixels, int width, int height) HBitmapToPixels(IntPtr hbm) {
    var hdc = CreateCompatibleDC(IntPtr.Zero);
    SelectObject(hdc, hbm);
    try {
      var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>() };
      if (GetDIBits(hdc, hbm, 0, 0, null!, ref bmi, 0) == 0)
        return (null, 0, 0);
      int w = bmi.biWidth, h = Math.Abs(bmi.biHeight);
      bmi.biBitCount = 32;
      bmi.biCompression = 0;
      bmi.biHeight = -h;
      var pixels = new byte[w * h * 4];
      if (GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0)
        return (null, 0, 0);
      // Straight BGRA → premultiplied BGRA (required by WriteableBitmap).
      for (int i = 0; i < pixels.Length; i += 4) {
        byte a = pixels[i + 3];
        if (a == 255)
          continue;
        if (a == 0) { pixels[i] = pixels[i + 1] = pixels[i + 2] = 0; continue; }
        pixels[i] = (byte)(pixels[i] * a / 255);
        pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
        pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
      }
      return (pixels, w, h);
    } finally { DeleteDC(hdc); }
  }

  /// <summary>
  /// Converts an HBITMAP directly to a WriteableBitmap (synchronous, UI thread).
  /// Used by ShellTreeView which loads icons on demand without a background pipeline.
  /// </summary>
  public static WriteableBitmap? HBitmapToWriteableBitmap(IntPtr hbm) {
    var (pixels, w, h) = HBitmapToPixels(hbm);
    return pixels is null ? null : PixelsToBitmapSync(pixels, w, h);
  }

  /// <summary>
  /// Creates a WriteableBitmap from premultiplied BGRA bytes. Must be called on the UI thread.
  /// </summary>
  public static WriteableBitmap? PixelsToBitmapSync(byte[] pixels, int w, int h) {
    if (w <= 0 || h <= 0 || pixels is null)
      return null;
    var wb = new WriteableBitmap(w, h);
    using var stream = wb.PixelBuffer.AsStream();
    stream.Write(pixels, 0, pixels.Length);
    wb.Invalidate();
    return wb;
  }

  /// <summary>
  /// Full async pipeline: get HBITMAP on thread-pool → copy pixels → create WriteableBitmap on UI thread.
  /// </summary>
  public static async Task<WriteableBitmap?> GetShellImageAsync(
      string path, uint size, SIIGBF flags, CancellationToken ct) {
    var (bmp, _) = await GetShellImageResultAsync(path, size, flags, ct);
    return bmp;
  }

  /// <summary>
  /// Like <see cref="GetShellImageAsync"/> but also returns the raw HRESULT.
  /// Callers can test <c>hr == NativeShell.E_PENDING</c> to know a retry is warranted.
  /// </summary>
  /// <summary>
  /// Returns raw pixel data and the HRESULT entirely off the UI thread.
  /// Call <see cref="PixelsToBitmapSync"/> on the UI thread to finalise.
  /// </summary>
  public static async Task<(byte[]? Pixels, int W, int H, int Hr)> GetShellImagePixelsAsync(
      string path, uint size, SIIGBF flags, CancellationToken ct) {
    // Acquire the global cap BEFORE entering Task.Run so a cancelled token
    // skips the wait entirely — stale workers never block a thread-pool thread.
    try { await _shellCallSem.WaitAsync(ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return (null, 0, 0, unchecked((int)0x80004004)); }
    try {
      return await Task.Run(() => {
        if (ct.IsCancellationRequested)
          return ((byte[]?)null, 0, 0, unchecked((int)0x80004004));
        int rc = TryGetShellHBitmapHr(path, size, flags, out var hbm);
        if (hbm == IntPtr.Zero)
          return ((byte[]?)null, 0, 0, rc);
        try {
          var (px, pw, ph) = HBitmapToPixels(hbm);
          return (px, pw, ph, rc);
        } finally { DeleteObject(hbm); }
      }, ct).ConfigureAwait(false);
    } finally { _shellCallSem.Release(); }
  }

  public static async Task<(WriteableBitmap? Bmp, int Hr)> GetShellImageResultAsync(
      string path, uint size, SIIGBF flags, CancellationToken ct) {
    var (pixels, w, h, hr) = await GetShellImagePixelsAsync(path, size, flags, ct)
        .ConfigureAwait(true);   // resume on UI thread for WriteableBitmap
    if (pixels is null || ct.IsCancellationRequested)
      return (null, hr);
    return (PixelsToBitmapSync(pixels, w, h), hr);
  }

  // ── Shell overlay icons ──────────────────────────────────────────────────

  /// <summary>
  /// Synchronous equivalent of <see cref="GetShellImagePixelsAsync"/> for use on
  /// dedicated background threads where blocking is acceptable.
  /// </summary>
  public static (byte[]? Pixels, int W, int H, int Hr) GetShellImagePixelsSync(
      string path, uint size, SIIGBF flags, CancellationToken ct) {
    try { _shellCallSem.Wait(ct); }
    catch (OperationCanceledException) { return (null, 0, 0, unchecked((int)0x80004004)); }
    try {
      if (ct.IsCancellationRequested)
        return (null, 0, 0, unchecked((int)0x80004004));
      int rc = TryGetShellHBitmapHr(path, size, flags, out var hbm);
      if (hbm == IntPtr.Zero)
        return (null, 0, 0, rc);
      try {
        var (px, pw, ph) = HBitmapToPixels(hbm);
        return (px, pw, ph, rc);
      } finally { DeleteObject(hbm); }
    } finally { _shellCallSem.Release(); }
  }

  /// <summary>
  /// Synchronous equivalent of <see cref="GetOverlayIconPixelsAsync"/> for use on
  /// dedicated background threads where blocking is acceptable.
  /// </summary>
  public static (byte[]? Pixels, int W, int H, int Slot) GetOverlayIconPixelsSync(
      string path, CancellationToken ct) {
    try { _shellCallSem.Wait(ct); }
    catch (OperationCanceledException) { return (null, 0, 0, 0); }
    try {
      if (ct.IsCancellationRequested) return (null, 0, 0, 0);
      return GetOverlayIconPixelsCore(path);
    } finally { _shellCallSem.Release(); }
  }


  /// <paramref name="path"/>, or <c>(null, 0, 0, 0)</c> when no overlay is registered.
  /// The <c>Slot</c> value (1-15) is stable per overlay handler and can be used to
  /// deduplicate bitmaps across items that share the same extension handler.
  /// Results are cached per slot so every handler's icon is only fetched once per process.
  /// </summary>
  public static async Task<(byte[]? Pixels, int W, int H, int Slot)> GetOverlayIconPixelsAsync(
      string path, CancellationToken ct) {
    try { await _shellCallSem.WaitAsync(ct).ConfigureAwait(false); }
    catch (OperationCanceledException) { return (null, 0, 0, 0); }
    try {
      return await Task.Run(() => {
        if (ct.IsCancellationRequested) return (null, 0, 0, 0);
        return GetOverlayIconPixelsCore(path);
      }, ct).ConfigureAwait(false);
    } finally { _shellCallSem.Release(); }
  }

  private static (byte[]? Pixels, int W, int H, int Slot) GetOverlayIconPixelsCore(string path) {
    // ── Step 1: obtain the overlay slot via PIDL (thread-pool MTA is fine here) ──
    IntPtr pidl = ILCreateFromPathW(path);
    if (pidl == IntPtr.Zero)
      return (null, 0, 0, 0);

    int overlaySlot;
    try {
      var sfi = new SHFILEINFO();
      SHGetFileInfo(pidl, 0, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
          SHGFI_SYSICONINDEX | SHGFI_OVERLAYINDEX | SHGFI_PIDL | 0x20 | 0x100);
      overlaySlot = (int)((uint)sfi.iIcon >> 24) & 0x0F;
      if (sfi.hIcon != IntPtr.Zero)
        DestroyIcon(sfi.hIcon);
    } finally {
      ILFree(pidl);
    }

    if (overlaySlot == 0)
      return (null, 0, 0, 0);

    // ── Step 2: fast path — pixel data already cached for this slot ──────────
    //  Also waits for any in-flight fetch of the same slot to complete so we
    //  never spin up two STA threads that both call SHGetImageList for the same
    //  system image list (a process-wide COM singleton whose RCW must not be
    //  released while another thread still holds a reference to it).
    lock (_overlayPixelCacheLock) {
      // Wait until no other thread is fetching this slot.
      // Use a timeout as a safety net: if the in-flight thread crashes without
      // clearing the flag, we re-check the cache and try again rather than blocking forever.
      while (_overlayPixelInFlight.Contains(overlaySlot))
        Monitor.Wait(_overlayPixelCacheLock, millisecondsTimeout: 2000);

      if (_overlayPixelCache.TryGetValue(overlaySlot, out var cached))
        return cached.Pixels is null
            ? (null, 0, 0, overlaySlot)
            : (cached.Pixels, cached.W, cached.H, overlaySlot);

      // Mark this slot as in-flight so concurrent callers wait above.
      _overlayPixelInFlight.Add(overlaySlot);
    }

    // ── Step 3: fetch icon on a dedicated STA thread ──────────────────────
    // IImageList is a free-threaded COM object in practice, but SHGetImageList
    // must be called from the same apartment that will use the interface.
    // Using a short-lived STA thread avoids the “COM object separated from its
    // underlying RCW” error that occurs when a cached RCW outlives its STA thread.
    byte[]? pixels = null;
    int pw = 0, ph = 0;
    Exception? innerEx = null;

    var staThread = new System.Threading.Thread(() => {
      try {
        IImageList? imageList = null;
        // Overlay images exist only in SHIL_EXTRALARGE (0x2); SHIL_JUMBO (0x4) does not
        // support GetOverlayImage and would return null pixels, poisoning the cache.
        if (SHGetImageList(0x2, IID_IImageList, out imageList) != 0 || imageList == null)
          return;
        try {
          if (imageList.GetOverlayImage(overlaySlot, out int ilIndex) != 0)
            return;
          const uint ILD_TRANSPARENT = 0x1;
          if (imageList.GetIcon(ilIndex, 0x00001000, out IntPtr hIcon) != 0 || hIcon == IntPtr.Zero)
            return;
          try {
            var (px, w, h) = HIconToPixels(hIcon, 0, 0);  // 0,0 = detect native size
            pixels = px;
            pw = w;
            ph = h;
          } finally {
            DestroyIcon(hIcon);
          }
        } finally {
          // Do NOT call Marshal.ReleaseComObject here. SHGetImageList returns the
          // process-wide system image list singleton; releasing its RCW disconnects
          // it for every other caller and causes the "COM object separated from its
          // underlying RCW" exception on concurrent threads.
          GC.KeepAlive(imageList);
        }
      } catch (Exception ex) {
        innerEx = ex;
      }
    });
    staThread.SetApartmentState(System.Threading.ApartmentState.STA);
    staThread.IsBackground = true;
    staThread.Start();
    staThread.Join();

    // Cache the result and clear the in-flight marker so waiting threads wake up.
    lock (_overlayPixelCacheLock) {
      _overlayPixelCache.TryAdd(overlaySlot, (pixels, pw, ph));
      _overlayPixelInFlight.Remove(overlaySlot);
      Monitor.PulseAll(_overlayPixelCacheLock);
    }

    return pixels is null ? (null, 0, 0, overlaySlot) : (pixels, pw, ph, overlaySlot);
  }

  /// <summary>
  /// Renders an HICON into a premultiplied BGRA byte array (top-down) via a temporary DIBSection.
  /// Pass <c>targetW = 0, targetH = 0</c> to auto-detect the icon's native pixel dimensions via
  /// <c>GetIconInfo</c>/<c>GetObject</c> so the icon is rendered at its true size without upscaling.
  /// </summary>
  private static (byte[]? pixels, int w, int h) HIconToPixels(IntPtr hIcon, int targetW, int targetH) {
    // When 0,0 is passed, derive the icon's native pixel dimensions so we never
    // upscale (which destroys quality).  SHIL_JUMBO gives 256×256 natively;
    // SHIL_EXTRALARGE gives 48×48.  Both are rendered at their true size.
    if ((targetW == 0 || targetH == 0) && GetIconInfo(hIcon, out var iconInfo)) {
      IntPtr hbmForSize = iconInfo.hbmColor != IntPtr.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
      if (hbmForSize != IntPtr.Zero && GetObject(hbmForSize, Marshal.SizeOf<BITMAP>(), out var bm) != 0) {
        targetW = Math.Max(bm.bmWidth,  1);
        // For a mask-only icon the bitmap stores both mask and inverted-mask stacked, so height is doubled.
        targetH = iconInfo.hbmColor != IntPtr.Zero
            ? Math.Max(bm.bmHeight, 1)
            : Math.Max(bm.bmHeight / 2, 1);
      }
      if (iconInfo.hbmMask  != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
      if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
    }
    if (targetW <= 0 || targetH <= 0) return (null, 0, 0);

    IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
    if (hdc == IntPtr.Zero) return (null, 0, 0);
    try {
      var bmi = new BITMAPINFOHEADER {
        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth = targetW,
        biHeight = -targetH,  // negative = top-down
        biPlanes = 1,
        biBitCount = 32,
        biCompression = 0
      };
      IntPtr hbm = CreateDIBSection(hdc, ref bmi, 0, out _, IntPtr.Zero, 0);
      if (hbm == IntPtr.Zero) return (null, 0, 0);
      try {
        IntPtr hOld = SelectObject(hdc, hbm);
        try {
          DrawIconEx(hdc, 0, 0, hIcon, targetW, targetH, 0, IntPtr.Zero, DI_NORMAL);
        } finally {
          SelectObject(hdc, hOld);
        }
        return HBitmapToPixels(hbm);
      } finally {
        DeleteObject(hbm);
      }
    } finally {
      DeleteDC(hdc);
    }
  }

  /// <summary>
  /// Single-shot non-blocking thumbnail request via <c>IShellItemImageFactory</c>.
  /// Returns <c>(bitmap, isPending)</c>. When <paramref name="isPending"/> is <see langword="true"/>
  /// the shell has started async generation but is not yet finished; the caller should
  /// re-enqueue the item and retry after a delay instead of blocking a thread.
  /// </summary>
  public static async Task<(WriteableBitmap? Bitmap, bool IsPending)> GetShellThumbnailAsync(
      string path, uint size, CancellationToken ct) {
    var (pixels, w, h, hr) = await Task.Run(() => {
      IShellItemImageFactory? factory = null;
      try {
        SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItemImageFactory, out factory);
        if (factory == null)
          return ((byte[]?)null, 0, 0, -1);

        var sz = new SIZE { cx = (int)size, cy = (int)size };
        int rc = factory.GetImage(sz, SIIGBF.ResizeToFit, out var hbm);

        if (rc == 0 && hbm != IntPtr.Zero) {
          try { var r = HBitmapToPixels(hbm); return (r.pixels, r.width, r.height, rc); } finally { DeleteObject(hbm); }
        }
        return ((byte[]?)null, 0, 0, rc);
      } catch (OperationCanceledException) { return ((byte[]?)null, 0, 0, -1); } catch { return ((byte[]?)null, 0, 0, -1); } finally {
        if (factory != null)
          try { Marshal.ReleaseComObject(factory); } catch { }
      }
    }, ct).ConfigureAwait(false);   // stay off the UI thread; caller marshals WriteableBitmap

    if (ct.IsCancellationRequested)
      return (null, false);
    bool pending = (hr == E_PENDING);
    if (pixels is null)
      return (null, pending);
    // Marshal only the final bitmap creation back to the UI thread.
    await Task.Yield();   // ensure we're not already on the UI thread after ConfigureAwait(false)
    return (PixelsToBitmapSync(pixels, w, h), false);
  }

  /// <summary>
  /// Legacy polling wrapper kept for any remaining callers; prefer
  /// <see cref="GetShellThumbnailAsync"/> with caller-side re-enqueue instead.
  /// </summary>
  public static async Task<WriteableBitmap?> GetShellThumbnailWithPollingAsync(
      string path, uint size,
      CancellationToken ct,
      int pollIntervalMs = 500,
      int timeoutMs = 20_000) {
    var deadline = Environment.TickCount64 + timeoutMs;
    while (!ct.IsCancellationRequested) {
      var (bmp, isPending) = await GetShellThumbnailAsync(path, size, ct);
      if (bmp != null)
        return bmp;
      if (!isPending)
        return null;
      if (Environment.TickCount64 >= deadline)
        return null;
      await Task.Delay(pollIntervalMs, ct).ConfigureAwait(false);
    }
    return null;
  }

  /// <summary>
  /// Synchronous thumbnail cache probe. Returns null pixels on cache miss or undersized entry.
  /// </summary>
  public static (byte[]? pixels, int w, int h) TryGetCachedPixels(string path, uint size) {
    var hbm = TryGetShellHBitmap(path, size, SIIGBF.CacheOnly_Thumb);
    if (hbm == IntPtr.Zero)
      return (null, 0, 0);
    try {
      var result = HBitmapToPixels(hbm);
      if (result.pixels != null && result.width < (int)size && result.height < (int)size)
        return (null, 0, 0);
      return result;
    } finally { DeleteObject(hbm); }
  }

  /// <summary>
  /// Probes the shell thumbnail disk-cache in parallel for every path in
  /// <paramref name="paths"/>.  Each slot in the returned array corresponds to
  /// the same index in <paramref name="paths"/>; slots that miss the cache are
  /// left as <c>(null, 0, 0)</c>.
  /// <para>
  /// The method runs entirely on thread-pool threads (no UI-thread work). The
  /// caller must call <see cref="PixelsToBitmapSync"/> on the UI thread to turn
  /// the raw pixel data into a <see cref="WriteableBitmap"/>.
  /// </para>
  /// </summary>
  public static (byte[]? Pixels, int W, int H)[] TryGetCachedPixelsBatch(
      IReadOnlyList<string?> paths, uint size, int maxDegreeOfParallelism, CancellationToken ct) {
    var results = new (byte[]? Pixels, int W, int H)[paths.Count];
    if (ct.IsCancellationRequested)
      return results;
    try {
      Parallel.For(0, paths.Count,
          new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = ct },
          i => {
            if (ct.IsCancellationRequested)
              return;
            var path = paths[i];
            if (string.IsNullOrEmpty(path))
              return;
            // Acquire the global shell-call cap synchronously.
            // Cache-only probes are fast but still block on COM; gating them
            // prevents stale batch workers from flooding the thread pool.
            if (!_shellCallSem.Wait(0)) {
              // If all slots are busy, wait with cancellation support.
              try { _shellCallSem.Wait(ct); }
              catch (OperationCanceledException) { return; }
            }
            try { results[i] = TryGetCachedPixels(path, size); }
            finally { _shellCallSem.Release(); }
          });
    } catch (OperationCanceledException) {
      // Cancellation is expected; return whatever was computed so far.
    }
    return results;
  }

  // ── Known-folder path helpers ─────────────────────────────────────────────

  public static string? SHGetKnownFolderPath(Guid folderId) {
    int hr = SHGetKnownFolderPathNative(folderId, 0, IntPtr.Zero, out var pPath);
    if (hr != 0 || pPath == IntPtr.Zero)
      return null;
    try { return Marshal.PtrToStringUni(pPath); } finally { Marshal.FreeCoTaskMem(pPath); }
  }

  public static string GetDownloadsFolder() {
    var path = SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"));
    return path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
  }

  // ── Quick Access enumeration ──────────────────────────────────────────────

  public static List<(string Name, string Path)> EnumerateQuickAccessFolders() {
    var result = new List<(string, string)>();
    var linksPath = SHGetKnownFolderPath(FOLDERID_Links);
    if (linksPath != null && Directory.Exists(linksPath)) {
      foreach (var lnk in Directory.EnumerateFiles(linksPath, "*.lnk")) {
        var target = ResolveShortcut(lnk);
        if (target != null && Directory.Exists(target))
          result.Add((Path.GetFileNameWithoutExtension(lnk), target));
      }
    }

    var standard = new[]
    {
            ("Desktop",   Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
            ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("Downloads", GetDownloadsFolder()),
            ("Pictures",  Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            ("Music",     Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            ("Videos",    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (n, p) in result)
      seen.Add(p);
    foreach (var (n, p) in standard)
      if (!string.IsNullOrEmpty(p) && seen.Add(p) && Directory.Exists(p))
        result.Add((n, p));

    return result;
  }

  // ── Shell-namespace breadcrumb helpers ────────────────────────────────────

  private const uint SIGDN_NORMALDISPLAY = 0x00000000;
  private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
  private const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

  /// <summary>
  /// A single breadcrumb segment returned by <see cref="BuildShellBreadcrumbs"/>.
  /// </summary>
  /// <param name="DisplayName">Friendly display name (e.g. "Local Disk (C:)", "This PC").</param>
  /// <param name="ParsingName">Shell parsing name to navigate to (filesystem path or ::{GUID}).</param>
  public record BreadcrumbSegment(string DisplayName, string ParsingName);

  /// <summary>
  /// Walks the shell namespace from <paramref name="path"/> to the desktop root and
  /// returns the ordered chain of <see cref="BreadcrumbSegment"/> values (root first).
  /// Virtual folders (This PC, Quick Access, …) are included with their correct
  /// display names and canonical ::{GUID} parsing names.
  /// </summary>
  public static List<BreadcrumbSegment> BuildShellBreadcrumbs(string path) {
    var chain = new List<BreadcrumbSegment>();
    if (string.IsNullOrEmpty(path))
      return chain;

    try {
      SHCreateItemFromParsingNameShell(path, IntPtr.Zero, IID_IShellItem, out var item);
      var current = item;
      int guard = 64;
      while (current != null && guard-- > 0) {
        string displayName;
        string parsingName;
        try { current.GetDisplayName(SIGDN_NORMALDISPLAY, out displayName); } catch { displayName = path; }
        try { current.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out parsingName); } catch { parsingName = path; }

        chain.Insert(0, new BreadcrumbSegment(displayName, parsingName));

        IShellItem? parent = null;
        try { current.GetParent(out parent); } catch {
          // Virtual roots like "This PC" have no parent — stop here with the single item.
          break;
        }

        if (parent == null)
          // Reached a root with no parent.
          break;

        // Stop if we have reached the desktop (its parsing name is just the desktop folder path)
        string parentParsing;
        try { parent.GetDisplayName(SIGDN_DESKTOPABSOLUTEPARSING, out parentParsing); } catch { break; }

        // Desktop root typically has no parent — detect cycle or empty name
        if (string.IsNullOrEmpty(parentParsing) || parentParsing == parsingName)
          break;

        current = parent;
      }
    } catch { }

    // The chain now contains the full breadcrumb path. Virtual roots (This PC, etc.)
    // will have a single-item chain. Desktop root is already in the chain (if applicable),
    // and the caller filters it if needed.
    return chain;
  }

  /// <summary>
  /// Returns the shell display name for a single path (filesystem or virtual).
  /// Falls back to <paramref name="path"/> itself on failure.
  /// </summary>
  public static string GetShellDisplayName(string path) {
    if (string.IsNullOrEmpty(path))
      return path;
    try {
      SHCreateItemFromParsingNameShell(path, IntPtr.Zero, IID_IShellItem, out var item);
      try {
        item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
        return string.IsNullOrEmpty(name) ? path : name;
      } finally { Marshal.ReleaseComObject(item); }
    } catch { return path; }
  }

  /// <summary>
  /// Resolves any shell parsing path to its real filesystem path via
  /// <c>SIGDN_FILESYSPATH</c>.  Returns <c>null</c> when the item has no
  /// filesystem representation (e.g. a purely virtual known folder).
  /// </summary>
  public static string? TryGetFileSystemPath(string parsingPath) {
    if (string.IsNullOrEmpty(parsingPath)) return null;
    try {
      SHCreateItemFromParsingNameShell(parsingPath, IntPtr.Zero, IID_IShellItem, out var item);
      try {
        item.GetDisplayName(SIGDN_FILESYSPATH, out var fsPath);
        return string.IsNullOrEmpty(fsPath) ? null : fsPath;
      } finally { Marshal.ReleaseComObject(item); }
    } catch { return null; }
  }

  /// <summary>
  /// Returns the display name for a virtual root path like ::{GUID}.
  /// For filesystem paths, delegates to <see cref="GetShellDisplayName"/>.
  /// </summary>
  public static string GetVirtualRootDisplayName(string path) {
    if (string.IsNullOrEmpty(path))
      return path;

    // If it's a virtual path (::{GUID}), try to resolve it reliably.
    if (path.StartsWith("::{", StringComparison.OrdinalIgnoreCase)) {
      try {
        // Extract GUID and look it up by known-folder IDs.
        var guidStr = path.TrimStart(':').TrimStart('{').TrimEnd('}');
        if (Guid.TryParse(guidStr, out var folderId)) {
          // Map known folder GUIDs to display names.
          if (folderId == FOLDERID_ComputerFolder)
            return "This PC";
          if (folderId == FOLDERID_QuickAccess)
            return "Quick Access";
          if (folderId == FOLDERID_NetworkFolder)
            return "Network";
          if (folderId == FOLDERID_Libraries)
            return "Libraries";

          // For other known folders, ask the shell.
          try {
            SHGetKnownFolderItem(folderId, 0, IntPtr.Zero, IID_IShellItem, out var item);
            try {
              item.GetDisplayName(SIGDN_NORMALDISPLAY, out var name);
              if (!string.IsNullOrEmpty(name))
                return name;
            } finally { Marshal.ReleaseComObject(item); }
          } catch { }
        }
      } catch { }
    }

    // Fallback to generic shell display name resolution.
    return GetShellDisplayName(path);
  }

  /// <summary>
  /// Enumerates direct sub-folders of a path (filesystem or virtual ::{GUID}).
  /// Returns a list of (DisplayName, ParsingName) pairs, sorted by display name.
  /// </summary>
  public static List<(string DisplayName, string ParsingName)> GetShellSubfolders(string path) {
    var result = new List<(string, string)>();
    if (string.IsNullOrEmpty(path))
      return result;

    // Virtual known-folder path — delegate to IShellFolder enumeration.
    if (path.StartsWith("::", StringComparison.Ordinal)) {
      try {
        SHCreateItemFromParsingNameShell(path, IntPtr.Zero, IID_IShellItem, out var folderItem);
        try {
          folderItem.BindToHandler(IntPtr.Zero, BHID_SFObject, IID_IShellFolder, out var folderObj);
          var folder = (IShellFolder)folderObj;
          try {
            if (folder.EnumObjects(IntPtr.Zero,
                    SHCONTF_FOLDERS | SHCONTF_FASTITEMS,
                    out var enumObj) == 0 && enumObj != null) {
              try {
                while (enumObj.Next(1, out var childPidl, out _) == 0) {
                  try {
                    var strretParsing = default(STRRET);
                    var strretDisplay = default(STRRET);
                    folder.GetDisplayNameOf(childPidl, SHGDN_FORPARSING, out strretParsing);
                    folder.GetDisplayNameOf(childPidl, 0, out strretDisplay);
                    string? parsingName  = StrRetToStr(ref strretParsing, childPidl);
                    string? displayName  = StrRetToStr(ref strretDisplay, childPidl);
                    if (!string.IsNullOrEmpty(parsingName) && !string.IsNullOrEmpty(displayName))
                      result.Add((displayName, parsingName));
                  } catch { } finally { CoTaskMemFree(childPidl); }
                }
              } finally { Marshal.ReleaseComObject(enumObj); }
            }
          } finally { Marshal.ReleaseComObject(folder); }
        } finally { Marshal.ReleaseComObject(folderItem); }
      } catch { }
    } else {
      // Filesystem path.
      try {
        foreach (var dir in Directory.EnumerateDirectories(path)) {
          var attrs = System.IO.File.GetAttributes(dir);
          if ((attrs & System.IO.FileAttributes.Hidden) != 0)
            continue;
          if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0)
            continue;
          var displayName = GetShellDisplayName(dir);
          result.Add((displayName, dir));
        }
      } catch { }
    }

    result.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
    return result;
  }

  // ── Shell-namespace parent resolution ────────────────────────────────────

  private const uint SIGDN_FILESYSPATH = 0x80058000;

  /// <summary>
  /// Walks the shell namespace to find the parent of <paramref name="currentPath"/>.
  /// Returns <c>(null, Guid.Empty)</c> when there is no meaningful parent.
  /// Returns <c>(path, Guid.Empty)</c> for a filesystem parent folder.
  /// Returns <c>(null, folderGuid)</c> when the parent is a known virtual folder
  /// (e.g. "This PC" for a drive root).
  /// </summary>
  public static (string? FilePath, Guid KnownFolderGuid) TryGetShellParent(string currentPath) {
    if (string.IsNullOrEmpty(currentPath))
      return (null, Guid.Empty);

    IntPtr parentPidl = IntPtr.Zero;
    try {
      // Create an IShellItem for the current location.  SHCreateItemFromParsingName
      // handles both regular filesystem paths and virtual ::{GUID} parsing names.
      SHCreateItemFromParsingNameShell(currentPath, IntPtr.Zero,
          IID_IShellItem, out var item);
      try {
        // Walk one level up in the shell namespace.
        item.GetParent(out var parent);
        if (parent == null)
          return (null, Guid.Empty);
        try {
          // Always try the filesystem path first — regular folders like C:\Windows or
          // C:\ have a real filesystem path even if they are also registered known folders
          // (e.g. FOLDERID_Windows, FOLDERID_Profile).  Preferring the filesystem path
          // keeps navigation inside LoadDirectory / Navigate rather than the slower
          // known-folder enumeration path, and keeps breadcrumb / tree sync working.
          try {
            parent.GetDisplayName(SIGDN_FILESYSPATH, out var fsPath);
            if (!string.IsNullOrEmpty(fsPath))
              return (fsPath, Guid.Empty);
          } catch { }

          // No filesystem path — parent is a purely virtual known folder (Desktop,
          // This PC, Libraries, Network…).  Resolve via IKnownFolderManager so we
          // get the canonical FOLDERID GUID rather than a raw CLSID.
          try {
            SHGetIDListFromObject(parent, out parentPidl);

            int hr = CoCreateInstance(CLSID_KnownFolderManager, IntPtr.Zero, 1,
                IID_IKnownFolderManager, out var kfmObj);
            if (hr == 0) {
              var kfm = (IKnownFolderManager)kfmObj;
              try {
                kfm.FindFolderFromIDList(parentPidl, out var kf);
                try {
                  kf.GetId(out var folderGuid);
                  return (null, folderGuid);
                } finally { Marshal.ReleaseComObject(kf); }
              } finally { Marshal.ReleaseComObject(kfm); }
            }
          } catch { } finally {
            if (parentPidl != IntPtr.Zero)
              CoTaskMemFree(parentPidl);
            parentPidl = IntPtr.Zero;
          }

          return (null, Guid.Empty);
        } finally { Marshal.ReleaseComObject(parent); }
      } finally { Marshal.ReleaseComObject(item); }
    } catch {
      // GetParent() fails (e.g. E_FAIL) when the item has no parent.
      return (null, Guid.Empty);
    } finally {
      if (parentPidl != IntPtr.Zero)
        CoTaskMemFree(parentPidl);
    }
  }

  // ── IShellFolder enumeration (virtual known folder navigation) ────────────

  public static List<ShellItem> EnumerateKnownFolderChildren(Guid folderId) {
    var result = new List<ShellItem>();

    // Network folder needs special handling: use SHGetDesktopFolder +
    // SHGetSpecialFolderLocation(CSIDL_NETWORK) so ALL categories are visible
    // (UPnP, WSD, printers, infrastructure), not just SMB computers.
    // The standard SFGAO_FILESYSTEM filter must also be skipped because network
    // devices are virtual items with no filesystem path.
    if (folderId == FOLDERID_NetworkFolder) {
      EnumerateNetworkFolderAsShellItems(result);
      return result;
    }

    try {
      IShellItem folderItem = null;
      try {
        SHGetKnownFolderItem(folderId, 0, IntPtr.Zero, IID_IShellItem, out folderItem);
      } catch {
        // SHGetKnownFolderItem can fail for some virtual folders (e.g. "This PC") if the desktop shell folder is not fully initialised yet.
        // In that case, we can still try to get the IShellFolder by parsing the known-folder path directly.
        var knownPath = "shell:::" + folderId.ToString("B").ToUpperInvariant();
        if (string.IsNullOrEmpty(knownPath))
          return result; // Can't get the path, give up.
        SHCreateItemFromParsingNameShell(knownPath, IntPtr.Zero, IID_IShellItem, out folderItem);
      }
      try {
        folderItem.BindToHandler(IntPtr.Zero, BHID_SFObject, IID_IShellFolder, out var folderObj);
        var folder = (IShellFolder)folderObj;
        try {
          if (folder.EnumObjects(IntPtr.Zero,
                  SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_FASTITEMS,
                  out var enumIdList) == 0 && enumIdList != null) {
            try {
              while (enumIdList.Next(1, out var childPidl, out _) == 0) {
                try {
                  var strret = default(STRRET);
                  folder.GetDisplayNameOf(childPidl, SHGDN_FORPARSING, out strret);
                  string? parsePath = StrRetToStr(ref strret, childPidl);
                  if (string.IsNullOrEmpty(parsePath))
                    continue;

                  var strretDisplay = default(STRRET);
                  folder.GetDisplayNameOf(childPidl, 0, out strretDisplay);
                  string? displayName = StrRetToStr(ref strretDisplay, childPidl);

                  uint attrs = SFGAO_FILESYSTEM | SFGAO_FOLDER;
                  folder.GetAttributesOf(1, [childPidl], ref attrs);
                  bool isFolder = (attrs & SFGAO_FOLDER) != 0;
                  bool isFs = (attrs & SFGAO_FILESYSTEM) != 0;

                  // For virtual folders (like This PC), items might not have SFGAO_FILESYSTEM set.
                  // Accept items that are folders OR have a valid filesystem path.
                  // Skip only if it's neither a folder nor a valid path.
                  if (!isFolder && !isFs && !Directory.Exists(parsePath))
                    continue;

                  var item = new ShellItem {
                    Name = displayName ?? Path.GetFileName(parsePath),
                    FullPath = parsePath,
                    IsFolder = isFolder,
                  };
                  // ThisPC children are drives / special folders: always use IShellItem2
                  // so display names like "Local Disk (C:)" are retrieved correctly.
                  // FindFirstFileEx does not work on drive roots.
                  EnrichFromIShellItem2(item, parsePath);
                  EnrichDriveSpace(item, parsePath);
                  result.Add(item);
                } catch { } finally { Marshal.FreeCoTaskMem(childPidl); }
              }
            } finally { Marshal.ReleaseComObject(enumIdList); }
          }
        } finally { Marshal.ReleaseComObject(folder); }
      } finally { Marshal.ReleaseComObject(folderItem); }
    } catch { }
    return result;
  }

  // Enumerates the Network shell folder and converts each child into a ShellItem.
  // Uses the SHGetDesktopFolder + CSIDL_NETWORK binding path (identical to
  // EnumerateNetworkResources) so ALL categories come back.  The SFGAO_FILESYSTEM
  // filter is intentionally omitted because network devices are virtual items.
  private static void EnumerateNetworkFolderAsShellItems(List<ShellItem> result) {
    IShellFolder? netFolder  = null;
    IntPtr        networkPidl = IntPtr.Zero;
    try {
      if (SHGetDesktopFolder(out var desktopObj) != 0) return;
      var desktop = (IShellFolder)desktopObj;
      try {
        if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_NETWORK, out networkPidl) != 0
            || networkPidl == IntPtr.Zero) return;
        desktop.BindToObject(networkPidl, IntPtr.Zero, IID_IShellFolder, out var netObj);
        netFolder = (IShellFolder)netObj;
      } finally { Marshal.ReleaseComObject(desktop); }
    } catch { return; }
    finally {
      if (networkPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(networkPidl);
    }

    if (netFolder == null) return;
    try {
      const uint flags = SHCONTF_FOLDERS | SHCONTF_NONFOLDERS
                       | SHCONTF_NETPRINTERSRCH | SHCONTF_SHAREABLE
                       | SHCONTF_ENABLE_ASYNC;
      if (netFolder.EnumObjects(IntPtr.Zero, flags, out var enumIdList) != 0
          || enumIdList == null) return;
      try {
        while (enumIdList.Next(1, out var childPidl, out _) == 0) {
          try {
            var strretParsing = default(STRRET);
            netFolder.GetDisplayNameOf(childPidl, SHGDN_FORPARSING, out strretParsing);
            string? parsePath = StrRetToStr(ref strretParsing, childPidl);
            if (string.IsNullOrEmpty(parsePath)) continue;

            var strretDisplay = default(STRRET);
            netFolder.GetDisplayNameOf(childPidl, 0, out strretDisplay);
            string? displayName = StrRetToStr(ref strretDisplay, childPidl);
            if (string.IsNullOrEmpty(displayName)) displayName = parsePath;

            uint attrs = SFGAO_FOLDER;
            netFolder.GetAttributesOf(1, [childPidl], ref attrs);
            bool isFolder = (attrs & SFGAO_FOLDER) != 0;

            string category = GetNetworkItemCategory(netFolder, childPidl, parsePath, isFolder);

            var shellItem = new ShellItem {
              Name          = displayName,
              DisplayName   = displayName,
              FullPath      = parsePath,
              IsFolder      = isFolder,
              ItemType      = category,
              IsNetworkItem = true,
            };
            result.Add(shellItem);
          } catch { } finally { Marshal.FreeCoTaskMem(childPidl); }
        }
      } finally { Marshal.ReleaseComObject(enumIdList); }
    } catch { }
    finally { Marshal.ReleaseComObject(netFolder); }
  }


  /// Works for virtual paths like <c>::{GUID}\foo.library-ms</c> that cannot be handled
  /// by <see cref="EnumerateKnownFolderChildren"/> or <c>FindFirstFileEx</c>.
  /// </summary>
  public static List<ShellItem> EnumerateShellItemChildrenByPath(string parsingPath) {
    var result = new List<ShellItem>();
    try {
      SHCreateItemFromParsingNameShell(parsingPath, IntPtr.Zero, IID_IShellItem, out var folderItem);
      try {
        folderItem.BindToHandler(IntPtr.Zero, BHID_SFObject, IID_IShellFolder, out var folderObj);
        var folder = (IShellFolder)folderObj;
        try {
          if (folder.EnumObjects(IntPtr.Zero,
                  SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_FASTITEMS,
                  out var enumIdList) == 0 && enumIdList != null) {
            try {
              while (enumIdList.Next(1, out var childPidl, out _) == 0) {
                try {
                  var strret = default(STRRET);
                  folder.GetDisplayNameOf(childPidl, SHGDN_FORPARSING, out strret);
                  string? parsePath = StrRetToStr(ref strret, childPidl);
                  if (string.IsNullOrEmpty(parsePath))
                    continue;

                  var strretDisplay = default(STRRET);
                  folder.GetDisplayNameOf(childPidl, 0, out strretDisplay);
                  string? displayName = StrRetToStr(ref strretDisplay, childPidl);

                  uint attrs = SFGAO_FILESYSTEM | SFGAO_FOLDER;
                  folder.GetAttributesOf(1, [childPidl], ref attrs);
                  bool isFolder = (attrs & SFGAO_FOLDER) != 0;
                  bool isFs = (attrs & SFGAO_FILESYSTEM) != 0;

                  if (!isFolder && !isFs && !Directory.Exists(parsePath))
                    continue;

                  var item = new ShellItem {
                    Name = displayName ?? Path.GetFileName(parsePath),
                    FullPath = parsePath,
                    IsFolder = isFolder,
                  };
                  // Library children are real filesystem paths — use FindFirstFileEx.
                  // Only use IShellItem2 for virtual items that have no FS path.
                  if (isFs)
                    EnrichFromFindFirstFile(item, parsePath);
                  else
                    EnrichFromIShellItem2(item, parsePath);
                  result.Add(item);
                } catch { } finally { Marshal.FreeCoTaskMem(childPidl); }
              }
            } finally { Marshal.ReleaseComObject(enumIdList); }
          }
        } finally { Marshal.ReleaseComObject(folder); }
      } finally { Marshal.ReleaseComObject(folderItem); }
    } catch { }
    return result;
  }

  /// <summary>
  /// Fills metadata on <paramref name="item"/> using <c>FindFirstFileEx</c> — much
  /// faster than <c>IShellItem2</c> for real filesystem paths because it avoids
  /// one COM creation call + four property-store reads per item.
  /// Falls back silently if the path is not accessible.
  /// </summary>
  private static void EnrichFromFindFirstFile(ShellItem item, string parsePath) {
    if (string.IsNullOrEmpty(parsePath)) return;
    try {
      var hFind = FindFirstFileEx(parsePath,
          FINDEX_INFO_BASIC, out var data, FINDEX_SEARCH_NAME, IntPtr.Zero, LARGE_FETCH);
      if (hFind == INVALID_HANDLE_VALUE) return;
      FindClose(hFind);

      if ((data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE) != 0) return;

      item.DateModified = DateTime.FromFileTimeUtc(data.ftLastWriteTime).ToLocalTime();
      item.IsHidden     = (data.dwFileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0;
      // Ensure DisplayName mirrors the name already set from IShellFolder.
      if (string.IsNullOrEmpty(item.DisplayName))
        item.DisplayName = item.Name;

      if (item.IsFolder) {
        item.ItemType = "File folder";
      } else {
        long size = ((long)data.nFileSizeHigh << 32) | (uint)data.nFileSizeLow;
        item.SizeBytes = size;
        item.Size      = FormatSize(size);
        item.ItemType  = GetItemTypeString(Path.GetExtension(parsePath));
      }
    } catch { }
  }

  /// <summary>
  /// Fills <see cref="ShellItem.ItemType"/>, <see cref="ShellItem.DateModified"/>,
  /// <see cref="ShellItem.Size"/>, and <see cref="ShellItem.SizeBytes"/> from the
  /// shell property store via <c>IShellItem2</c>.  Safe to call for both real filesystem
  /// paths and virtual/shell-namespace paths (ThisPC drives, Libraries, etc.).
  /// Does nothing if the COM call fails.
  /// </summary>
  private static void EnrichFromIShellItem2(ShellItem item, string parsePath) {
    try {
      SHCreateItemFromParsingNameItem2(parsePath, IntPtr.Zero, IID_IShellItem2, out var si2);
      if (si2 is null) return;

      // Display name — only overwrite if the shell gives a richer name than the
      // IShellFolder display name we already have (e.g. "Local Disk (C:)" vs "C:").
      try {
        if (si2.GetDisplayName(SIGDN.NORMALDISPLAY, out var shellName) == HResult.S_OK
            && !string.IsNullOrEmpty(shellName)) {
          item.Name = shellName;
          item.DisplayName = shellName;
        }
      } catch { }

      // Item type text ("File folder", "Local Disk", "System Folder", …)
      try {
        var pk = PKEY_ItemTypeText;
        if (si2.GetString(ref pk, out var typeText) == HResult.S_OK
            && !string.IsNullOrEmpty(typeText))
          item.ItemType = typeText;
      } catch { }

      // Date modified
      try {
        var pk = PKEY_DateModified;
        si2.GetFileTime(ref pk, out var ft);
        long ticks = (((long)ft.dwHighDateTime) << 32) | (uint)ft.dwLowDateTime;
        if (ticks > 0)
          item.DateModified = DateTime.FromFileTimeUtc(ticks).ToLocalTime();
      } catch { }

      // Size (only meaningful for files)
      if (!item.IsFolder) {
        try {
          var pk = PKEY_Size;
          if (si2.GetUInt64(ref pk, out ulong sizeBytes) == HResult.S_OK && sizeBytes > 0) {
            item.SizeBytes = (long)sizeBytes;
            item.Size      = FormatSize((long)sizeBytes);
          }
        } catch { }
      }
    } catch { }
  }

  // ── FindFirstFileEx directory enumeration ─────────────────────────────────

  // Cache shell-friendly type strings ("PNG Image", "Text Document") keyed by extension.
  // SHGetFileInfo with SHGFI_USEFILEATTRIBUTES|SHGFI_TYPENAME reads from the registry
  // only (no file access), so it is safe to call on any thread and is fast per extension.
  private static readonly Dictionary<string, string> _extTypeStringCache =
      new(StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Populates drive-space properties on <paramref name="item"/> when the item
  /// maps to an accessible Windows drive root (e.g. "C:\").
  /// No-ops and leaves the item untouched if the path is not a drive root or
  /// the drive is not ready (removable media not inserted, etc.).
  /// </summary>
  private static void EnrichDriveSpace(ShellItem item, string parsePath) {
    // Only roots like "C:\" qualify; virtual paths, UNC shares, etc. are handled below.
    try {
      // Normalise: accept "C:", "C:\", "\\server\share", etc.
      var norm = parsePath?.TrimEnd('\\', '/');
      if (string.IsNullOrEmpty(norm)) return;

      DriveInfo? di = null;
      DriveType driveType = DriveType.Unknown;

      // Try to build a DriveInfo for a standard single-letter drive root.
      if (norm.Length == 2 && norm[1] == ':') {
        di = new DriveInfo(norm);
        driveType = di.DriveType;
      } else if (norm.Length == 3 && norm[1] == ':' && (norm[2] == '\\' || norm[2] == '/')) {
        di = new DriveInfo(norm);
        driveType = di.DriveType;
      } else {
        // Network paths or device paths — attempt to match via DriveInfo enumeration.
        foreach (var d in DriveInfo.GetDrives()) {
          if (string.Equals(d.RootDirectory.FullName.TrimEnd('\\'),
                            norm, StringComparison.OrdinalIgnoreCase)) {
            di = d;
            driveType = d.DriveType;
            break;
          }
        }
      }

      if (di == null) return;

      // Classify for grouping header (mirrors Windows Explorer labels).
      item.DriveGroupType = driveType switch {
        DriveType.Removable or DriveType.CDRom => "Devices and drives",
        DriveType.Network                       => "Network locations",
        DriveType.Fixed                         => "Devices and drives",
        _                                       => "Devices and drives",
      };

      item.IsDrive = true;

      if (!di.IsReady) return; // removable media not inserted — leave space at 0

      long total = di.TotalSize;
      long free  = di.TotalFreeSpace;
      item.DriveTotalBytes = total;
      item.DriveUsedBytes  = total - free;
    } catch { }
  }

  public static string GetItemTypeStringPublic(string ext) => GetItemTypeString(ext);
  private static string GetItemTypeString(string ext) {
    if (string.IsNullOrEmpty(ext) || ext.Length <= 1)
      return "File";
    if (_extTypeStringCache.TryGetValue(ext, out var cached))
      return cached;

    // Ask the shell for the friendly type name. SHGFI_USEFILEATTRIBUTES means the
    // file is never opened — only the extension drives the registry lookup.
    string result;
    try {
      var dummy = "x" + ext; // e.g. "x.png"
      var sfi   = new SHFILEINFO();
      SHGetFileInfo(dummy, 0x80 /* FILE_ATTRIBUTE_NORMAL */, ref sfi,
          (uint)Marshal.SizeOf<SHFILEINFO>(),
          SHGFI_USEFILEATTRIBUTES | SHGFI_TYPENAME);
      result = !string.IsNullOrWhiteSpace(sfi.szTypeName)
          ? sfi.szTypeName
          : ext[1..].ToUpperInvariant() + " file";
    } catch {
      result = ext[1..].ToUpperInvariant() + " file";
    }

    _extTypeStringCache[ext] = result;
    return result;
  }

  public static (List<ShellItem> folders, List<ShellItem> files)
      EnumerateWithFindFirstFileEx(string path, CancellationToken ct) {
    List<ShellItem> folders = [], files = [];
    var hFind = FindFirstFileEx(Path.Combine(path, "*"),
        FINDEX_INFO_BASIC, out var data, FINDEX_SEARCH_NAME, IntPtr.Zero, LARGE_FETCH);
    if (hFind == INVALID_HANDLE_VALUE)
      return (folders, files);
    int count = 0;
    try {
      do {
        // Check cancellation every 64 items to avoid per-item overhead.
        if ((count++ & 63) == 0)
          ct.ThrowIfCancellationRequested();

        var name = data.cFileName;
        if (name is "." or "..")
          continue;
        var attrs = data.dwFileAttributes;
        if ((attrs & FILE_ATTRIBUTE_REPARSE) != 0)
          continue;
        var fullPath = Path.Combine(path, name);
        bool isDir = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
        bool isHidden = (attrs & FILE_ATTRIBUTE_HIDDEN) != 0;
        var modified = DateTime.FromFileTimeUtc(data.ftLastWriteTime).ToLocalTime();
        if (isDir) {
          folders.Add(new ShellItem {
            Name = name,
            FullPath = fullPath,
            ItemType = "File folder",
            IsFolder = true,
            IsHidden = isHidden,
            DateModified = modified
          });
        } else {
          long size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
          var ext = Path.GetExtension(name);
          files.Add(new ShellItem {
            Name = name,
            FullPath = fullPath,
            ItemType = GetItemTypeString(ext),
            IsFolder = false,
            IsHidden = isHidden,
            Size = FormatSize(size),
            SizeBytes = size,
            DateModified = modified
          });
        }
      }
      while (FindNextFile(hFind, out data));
    } finally { FindClose(hFind); }
    return (folders, files);
  }

  /// <summary>
  /// Returns a single <see cref="ShellItem"/> for <paramref name="fullPath"/>, populated
  /// from the file system the same way <see cref="EnumerateWithFindFirstFileEx"/> does.
  /// Returns <see langword="null"/> when the path does not exist or cannot be read.
  /// Safe to call from any thread.
  /// </summary>
  public static ShellItem? GetSingleItemMetadata(string fullPath) {
    try {
      // Use FindFirstFileEx for attrs / size / date: reads directly from the
      // filesystem buffer so it reflects in-progress copy sizes immediately,
      // and works even when the file handle is open (no sharing violation).
      var hFind = FindFirstFileEx(fullPath,
          FINDEX_INFO_BASIC, out var data, FINDEX_SEARCH_NAME, IntPtr.Zero, LARGE_FETCH);
      if (hFind == INVALID_HANDLE_VALUE)
        return null;
      FindClose(hFind);

      var name  = data.cFileName;
      var attrs = data.dwFileAttributes;
      if ((attrs & FILE_ATTRIBUTE_REPARSE) != 0)
        return null;

      bool isDir    = (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0;
      bool isHidden = (attrs & FILE_ATTRIBUTE_HIDDEN)    != 0;
      var  modified = DateTime.FromFileTimeUtc(data.ftLastWriteTime).ToLocalTime();

      if (isDir) {
        return new ShellItem {
          Name         = name,
          FullPath     = fullPath,
          ItemType     = "File folder",
          IsFolder     = true,
          IsHidden     = isHidden,
          DateModified = modified
        };
      }

      long size = ((long)data.nFileSizeHigh << 32) | (uint)data.nFileSizeLow;

      // IShellItem2.GetString(PKEY_ItemTypeText) gives the shell's own friendly
      // type string ("PNG File", "Text Document", etc.) — better than a raw
      // extension lookup.  Fall back to extension lookup if the COM call fails.
      string? typeText = null;
      try {
        SHCreateItemFromParsingNameItem2(fullPath, IntPtr.Zero, IID_IShellItem2, out var si2);
        if (si2 is not null) {
          var pk = PKEY_ItemTypeText;
          si2.GetString(ref pk, out typeText);
        }
      } catch { /* COM unavailable / locked — fall through */ }

      if (string.IsNullOrEmpty(typeText))
        typeText = GetItemTypeString(Path.GetExtension(name));

      return new ShellItem {
        Name         = name,
        FullPath     = fullPath,
        ItemType     = typeText,
        IsFolder     = false,
        IsHidden     = isHidden,
        Size         = FormatSize(size),
        SizeBytes    = size,
        DateModified = modified
      };
    } catch { return null; }
  }

  // ── Windows Search via ISearchFolderItemFactory (STA thread) ─────────────

  /// <summary>
  /// Searches <paramref name="folderPath"/> recursively for items whose names match
  /// <paramref name="query"/> using the shell <c>ISearchFolderItemFactory</c> COM interface
  /// on a dedicated STA thread (as required by shell COM).
  /// Glob patterns (<c>*.exe</c>, <c>doc?</c>) match the whole file name;
  /// plain text performs a contains match.
  /// </summary>
  /// <summary>
  /// Streaming variant of <see cref="SearchFolderAsync"/>: returns a
  /// <see cref="ChannelReader{T}"/> that yields <see cref="ShellItem"/> results
  /// as they are found on an STA worker thread, so the caller can display each
  /// batch immediately without waiting for the full enumeration to complete.
  /// </summary>
  public static ChannelReader<ShellItem> SearchFolderStreamAsync(
      string folderPath, string query, CancellationToken ct) {
    var channel = Channel.CreateUnbounded<ShellItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    if (string.IsNullOrWhiteSpace(folderPath)
        || folderPath.StartsWith("::", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(query)
        || !Directory.Exists(folderPath)) {
      channel.Writer.Complete();
      return channel.Reader;
    }

    bool isGlob = query.Contains('*') || query.Contains('?');
    string regexPattern = isGlob
        ? "^" + Regex.Escape(query).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"
        : Regex.Escape(query);
    var nameRegex = new Regex(regexPattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    var thread = new Thread(() => {
      try {
        ct.ThrowIfCancellationRequested();
        DoShellItemSearchStreaming(folderPath, nameRegex, query, isGlob, ct, channel.Writer);
        channel.Writer.Complete();
      } catch (OperationCanceledException) {
        channel.Writer.Complete();
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine($"[SearchFolderStreamAsync] {ex.GetType().Name}: {ex.Message}");
        channel.Writer.Complete();
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    return channel.Reader;
  }

  public static async Task<List<ShellItem>> SearchFolderAsync(
      string folderPath, string query, CancellationToken ct) {
    if (string.IsNullOrWhiteSpace(folderPath)
        || folderPath.StartsWith("::", StringComparison.Ordinal)
        || string.IsNullOrWhiteSpace(query)
        || !Directory.Exists(folderPath))
      return [];

    bool isGlob = query.Contains('*') || query.Contains('?');
    string regexPattern = isGlob
        ? "^" + Regex.Escape(query).Replace(@"\*", ".*").Replace(@"\?", ".") + "$"
        : Regex.Escape(query);
    var nameRegex = new Regex(regexPattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    var tcs = new TaskCompletionSource<List<ShellItem>>();
    var thread = new Thread(() => {
      try {
        ct.ThrowIfCancellationRequested();
        tcs.SetResult(DoShellItemSearch(folderPath, nameRegex, query, isGlob, ct));
      } catch (OperationCanceledException) {
        tcs.SetCanceled(ct);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine($"[SearchFolderAsync] {ex.GetType().Name}: {ex.Message}");
        tcs.SetResult([]);
      }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();

    return await tcs.Task.ConfigureAwait(false);
  }

  private static String PrepareSearchQuery(String query) {
    var prefix = "System.Generic.String:";
    if (query.StartsWith("*.")) {
      prefix = "fileextension:";
    }

    if (query.Contains(":")) {
      prefix = String.Empty;
    }

    return prefix + query;
  }

  /// <summary>
  /// COM/STA worker: creates a <c>SearchFolderItemFactory</c> scoped to
  /// <paramref name="folderPath"/> with an <c>ICondition</c> (built via
  /// <c>IConditionFactory2.CreateStringLeaf</c>) that targets
  /// <c>System.ItemNameDisplay</c> using DOS-wildcard or contains matching.
  /// Setting <c>FOLDERTYPEID_GenericSearchResults</c> and providing an explicit
  /// <c>ICondition</c> causes Windows Search to crawl non-indexed locations
  /// in addition to the index, so the results match what Explorer's search box
  /// returns for any folder.
  /// </summary>
  private static List<ShellItem> DoShellItemSearch(
      string folderPath, Regex nameRegex, string rawQuery, bool isGlob, CancellationToken ct) {
    var results = new List<ShellItem>();



    var factory = (BExplorer.Shell.Interop.ISearchFolderItemFactory)new SearchFolderItemFactoryCoClass();
    var searchCondition = SearchConditionFactory.ParseStructuredQuery(PrepareSearchQuery(rawQuery));
    var shellItems = new List<ShellLibrary.Interop.IShellItem>(1);
    //SHCreateItemFromParsingNameShell(folderPath, IntPtr.Zero, IID_IShellItem, out var folderItem);
    var folderItem = Shell32.SHCreateItemFromParsingName(folderPath, IntPtr.Zero, IID_IShellItem);
    shellItems.Add(folderItem);
    IShellItemArray scopeShellItemArray = new ShellItemArray(shellItems.ToArray());
    //var hr = SHCreateShellItemArrayFromShellItem(folderItem, IID_IShellItemArray, out var scopeObj);

    //if (hr == 0 && scopeObj is not null)
      factory.SetScope(scopeShellItemArray);

    // Pass the condition (or null to match everything).
    factory.SetCondition(searchCondition.NativeSearchCondition);


    // ── 3. Enumerate the virtual search-results IShellFolder ──────────────
    var hr = factory.GetShellItem(IID_IShellItem, out var searchItemObj);
    if (hr != 0 || searchItemObj is not IShellItem searchItem)
      return results;

    try {
      searchItem.BindToHandler(IntPtr.Zero, BHID_SFObject, IID_IShellFolder, out var sfObj);
      if (sfObj is not IShellFolder sf)
        return results;

      try {
        hr = sf.EnumObjects(IntPtr.Zero,
            (uint)(SHCONTF.FOLDERS | SHCONTF.INCLUDEHIDDEN | SHCONTF.INCLUDESUPERHIDDEN |
              SHCONTF.NONFOLDERS | SHCONTF.FASTITEMS),
            out var enumIDList);
        if (hr != 0 || enumIDList is null)
          return results;

        try {
          while (true) {
            ct.ThrowIfCancellationRequested();
            hr = enumIDList.Next(1, out var childPidl, out var fetched);
            if (hr != 0 || fetched == 0) break;

            try {
              var strret = default(STRRET);
              sf.GetDisplayNameOf(childPidl, SHGDN_FORPARSING, out strret);
              string? fullPath = StrRetToStr(ref strret, childPidl);

              if (string.IsNullOrEmpty(fullPath)) continue;
              string name = Path.GetFileName(fullPath);
              if (string.IsNullOrEmpty(name)) continue;

              var item = GetSingleItemMetadata(fullPath);
              if (item is not null) results.Add(item);
            } finally {
              Marshal.FreeCoTaskMem(childPidl);
            }
          }
        } finally { Marshal.ReleaseComObject(enumIDList); }
      } finally { Marshal.ReleaseComObject(sf); }
    } finally { Marshal.ReleaseComObject(searchItem); }

    return results;
  }

  /// <summary>
  /// Streaming variant of <see cref="DoShellItemSearch"/>: writes each matching
  /// <see cref="ShellItem"/> to <paramref name="writer"/> as soon as it is found
  /// so the consumer can display results incrementally.
  /// </summary>
  private static void DoShellItemSearchStreaming(
      string folderPath, Regex nameRegex, string rawQuery, bool isGlob,
      CancellationToken ct, ChannelWriter<ShellItem> writer) {
    var factory = (BExplorer.Shell.Interop.ISearchFolderItemFactory)new SearchFolderItemFactoryCoClass();
    var searchCondition = SearchConditionFactory.ParseStructuredQuery(PrepareSearchQuery(rawQuery));
    var shellItems = new List<ShellLibrary.Interop.IShellItem>(1);
    var folderItem = Shell32.SHCreateItemFromParsingName(folderPath, IntPtr.Zero, IID_IShellItem);
    shellItems.Add(folderItem);
    IShellItemArray scopeShellItemArray = new ShellItemArray(shellItems.ToArray());
    factory.SetScope(scopeShellItemArray);
    factory.SetCondition(searchCondition.NativeSearchCondition);

    var hr = factory.GetShellItem(IID_IShellItem, out var searchItemObj);
    if (hr != 0 || searchItemObj is not IShellItem searchItem)
      return;

    try {
      searchItem.BindToHandler(IntPtr.Zero, BHID_SFObject, IID_IShellFolder, out var sfObj);
      if (sfObj is not IShellFolder sf)
        return;

      try {
        hr = sf.EnumObjects(IntPtr.Zero,
            (uint)(SHCONTF.FOLDERS | SHCONTF.INCLUDEHIDDEN | SHCONTF.INCLUDESUPERHIDDEN |
                   SHCONTF.NONFOLDERS | SHCONTF.FASTITEMS),
            out var enumIDList);
        if (hr != 0 || enumIDList is null)
          return;

        try {
          while (true) {
            ct.ThrowIfCancellationRequested();
            hr = enumIDList.Next(1, out var childPidl, out var fetched);
            if (hr != 0 || fetched == 0) break;

            try {
              var strret = default(STRRET);
              sf.GetDisplayNameOf(childPidl, SHGDN_FORPARSING, out strret);
              string? fullPath = StrRetToStr(ref strret, childPidl);

              if (string.IsNullOrEmpty(fullPath)) continue;
              string name = Path.GetFileName(fullPath);
              if (string.IsNullOrEmpty(name)) continue;

              var item = GetSingleItemMetadata(fullPath);
              if (item is not null)
                writer.TryWrite(item);
            } finally {
              Marshal.FreeCoTaskMem(childPidl);
            }
          }
        } finally { Marshal.ReleaseComObject(enumIDList); }
      } finally { Marshal.ReleaseComObject(sf); }
    } finally { Marshal.ReleaseComObject(searchItem); }
  }

    // ── Library XML parser ────────────────────────────────────────────────────

  public static string? ResolveLibraryDefaultPath(string libraryFile) {
    try {
      var doc = XDocument.Load(libraryFile);
      XNamespace ns = "http://schemas.microsoft.com/windows/2009/library";
      foreach (var conn in doc.Descendants(ns + "searchConnectorDescription")) {
        var isDefault = conn.Element(ns + "isDefaultSaveLocation")?.Value;
        if (!string.Equals(isDefault, "true", StringComparison.OrdinalIgnoreCase))
          continue;

        var url = conn.Descendants(ns + "url").FirstOrDefault()?.Value
               ?? conn.Descendants(ns + "simpleLocation")
                      .Select(e => e.Element(ns + "url")?.Value)
                      .FirstOrDefault(v => v != null);
        if (url == null)
          continue;

        if (url.StartsWith("knownfolder:", StringComparison.OrdinalIgnoreCase)) {
          var guidStr = url["knownfolder:".Length..];
          if (Guid.TryParse(guidStr, out var guid))
            return SHGetKnownFolderPath(guid);
        } else if (Directory.Exists(url)) {
          return url;
        }
      }
    } catch { }
    return null;
  }

  // ── .lnk shortcut resolver ────────────────────────────────────────────────

  public static string? ResolveShortcut(string lnkPath) {
    try {
      int hr = CoCreateInstance(CLSID_ShellLink, IntPtr.Zero, 1,
          IID_IShellLinkW, out var obj);
      if (hr != 0)
        return null;
      try {
        var link = (IShellLinkW)obj;
        var pf = (IPersistFile)obj;
        pf.Load(lnkPath, 0);
        link.Resolve(IntPtr.Zero, 0x1);
        var sb = new System.Text.StringBuilder(260);
        link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
        return sb.Length > 0 ? sb.ToString() : null;
      } finally { Marshal.ReleaseComObject(obj); }
    } catch { return null; }
  }

  // ── Shell icon fallback (SHGetFileInfo + DrawIconEx) ─────────────────────

  private static IntPtr TryIconToBitmap(string virtualPath, int size) {
    var sfi = new SHFILEINFO();
    uint flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
    IntPtr ret = SHGetFileInfo(virtualPath, 0, ref sfi,
        (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
    if (ret == IntPtr.Zero || sfi.hIcon == IntPtr.Zero)
      return IntPtr.Zero;
    try {
      var bmi = new BITMAPINFOHEADER {
        biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth = size,
        biHeight = -size,
        biPlanes = 1,
        biBitCount = 32,
        biCompression = 0,
      };
      var hdc = CreateCompatibleDC(IntPtr.Zero);
      var hbm = CreateDIBSection(hdc, ref bmi, 0, out _, IntPtr.Zero, 0);
      var hOld = SelectObject(hdc, hbm);
      DrawIconEx(hdc, 0, 0, sfi.hIcon, size, size, 0, IntPtr.Zero, DI_NORMAL);
      SelectObject(hdc, hOld);
      DeleteDC(hdc);
      return hbm;
    } finally { DestroyIcon(sfi.hIcon); }
  }

  /// <summary>
  /// Returns an HBITMAP for the type-icon of <paramref name="ext"/> (e.g. <c>".jpg"</c>)
  /// or a folder when <paramref name="isFolder"/> is <see langword="true"/>,
  /// using <c>SHGFI_USEFILEATTRIBUTES</c> so the call never opens or stats any file.
  /// This is safe to call for cloud-only (dehydrated) OneDrive items.
  /// Caller is responsible for <c>DeleteObject</c>.
  /// </summary>
  public static IntPtr TryGetTypeIconHBitmap(string ext, bool isFolder, uint size) {
    try {
      // SHGetFileInfo with SHGFI_USEFILEATTRIBUTES uses the path only for its
      // extension; the file need not exist. Pass a dummy name with the right ext.
      string dummy = isFolder ? "folder" : ("x" + ext);
      uint attrs = isFolder ? FILE_ATTRIBUTE_DIRECTORY : 0;
      uint flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES |
                     (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
      var sfi = new SHFILEINFO();
      IntPtr ret = SHGetFileInfo(dummy, attrs, ref sfi,
          (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
      if (ret == IntPtr.Zero || sfi.hIcon == IntPtr.Zero)
        return IntPtr.Zero;
      try {
        int px = (int)size;
        var bmi = new BITMAPINFOHEADER {
          biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
          biWidth = px,
          biHeight = -px,
          biPlanes = 1,
          biBitCount = 32,
          biCompression = 0,
        };
        var hdc = CreateCompatibleDC(IntPtr.Zero);
        var hbm = CreateDIBSection(hdc, ref bmi, 0, out _, IntPtr.Zero, 0);
        var hOld = SelectObject(hdc, hbm);
        DrawIconEx(hdc, 0, 0, sfi.hIcon, px, px, 0, IntPtr.Zero, DI_NORMAL);
        SelectObject(hdc, hOld);
        DeleteDC(hdc);
        return hbm;
      } finally { DestroyIcon(sfi.hIcon); }
    } catch { return IntPtr.Zero; }
  }

  // ── FormatSize helper ─────────────────────────────────────────────────────

  // Windows Explorer always shows file sizes in KB, rounded up to the nearest KB,
  // with a thousands separator — e.g. "1 KB", "12 KB", "1,234 KB".
  // Zero-byte files show as "0 KB". Folders (bytes == -1 or 0) are left blank by the caller.
  public static string FormatSize(long bytes) {
    if (bytes <= 0) return "0 KB";
    long kb = (bytes + 1023) / 1024;   // ceiling division → always at least 1 KB
    return $"{kb:N0} KB";
  }

  // ── Network (shell namespace) ─────────────────────────────────────────────────────────────
  //
  // Real Windows Explorer enumerates the Network neighbourhood by walking the shell namespace
  // rooted at FOLDERID_NetworkFolder via IShellFolder.  This gives ALL device categories
  // (Computers, Media devices, Infrastructure, Printers, Other devices…) exactly as Explorer
  // shows them — something WNetOpenEnum can never do because it only sees disk shares.
  // WNet constants are kept only for the sub-level share enumeration under a specific server.

  private const uint RESOURCETYPE_ANY        = 0x00000000;  // all resource types
  private const uint RESOURCETYPE_DISK       = 0x00000001;
  private const uint RESOURCETYPE_PRINT      = 0x00000002;
  private const uint RESOURCEUSAGE_CONTAINER = 0x00000002;
  private const uint SCOPE_GLOBALNET         = 0x00000002;
  private const int  NET_NO_ERROR            = 0;
  private const int  ERROR_NO_MORE_ITEMS     = 259;

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct NETRESOURCE {
    public uint dwScope;
    public uint dwType;
    public uint dwDisplayType;
    public uint dwUsage;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpLocalName;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpRemoteName;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpComment;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpProvider;
  }

  [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
  private static extern int WNetOpenEnum(
      uint dwScope, uint dwType, uint dwUsage,
      ref NETRESOURCE lpNetResource, out IntPtr lphEnum);

  // Overload that accepts a null (IntPtr.Zero) network resource for top-level enumeration.
  [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetOpenEnumW")]
  private static extern int WNetOpenEnumNull(
      uint dwScope, uint dwType, uint dwUsage,
      IntPtr lpNetResource, out IntPtr lphEnum);

  [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
  private static extern int WNetEnumResource(
      IntPtr hEnum, ref uint lpcCount, IntPtr lpBuffer, ref uint lpBufferSize);

  [DllImport("mpr.dll")]
  private static extern int WNetCloseEnum(IntPtr hEnum);

  // dwDisplayType values (mpr.h RESOURCEDISPLAYTYPE_*).
  private const uint RESOURCEDISPLAYTYPE_NETWORK   = 0x00;
  private const uint RESOURCEDISPLAYTYPE_DOMAIN    = 0x01;
  private const uint RESOURCEDISPLAYTYPE_SERVER    = 0x02;
  private const uint RESOURCEDISPLAYTYPE_SHARE     = 0x03;
  private const uint RESOURCEDISPLAYTYPE_FILE      = 0x04;
  private const uint RESOURCEDISPLAYTYPE_GROUP     = 0x05;
  private const uint RESOURCEDISPLAYTYPE_NDSCONTAINER = 0x06;
  private const uint RESOURCEDISPLAYTYPE_TREE      = 0x07;
  private const uint RESOURCEDISPLAYTYPE_DIRECTORY = 0x09;

  /// <summary>Result item from a network neighbourhood enumeration.</summary>
  public sealed class NetworkResource {
    public required string RemoteName   { get; set; }
    public required string DisplayName  { get; set; }
    /// <summary>Shell-friendly device category, e.g. "Computer", "Media device".</summary>
    public required string Category     { get; set; }
    /// <summary>True = this node can be expanded (server / workgroup / device group).</summary>
    public bool IsContainer { get; set; }
    /// <summary>True = this is a navigable disk share (UNC path).</summary>
    public bool IsShare     { get; set; }
  }

  /// <summary>
  /// Enumerates the Network neighbourhood using the same approach as the original
  /// BExplorer.Shell library: <c>SHGetDesktopFolder</c> +
  /// <c>SHGetSpecialFolderLocation(CSIDL_NETWORK)</c> +
  /// <c>IShellFolder.BindToObject</c> + <c>EnumObjects</c> with
  /// <c>NETPRINTERSRCH | SHAREABLE</c> flags.
  ///
  /// Top-level (<paramref name="parentParsingPath"/> == null):
  ///   Returns every direct child: workgroups, UPnP device groups, media devices,
  ///   printers, infrastructure nodes, etc. — exactly as Explorer shows them.
  ///
  /// Sub-container (non-null <paramref name="parentParsingPath"/>):
  ///   Returns the direct children of that shell parsing path.
  ///
  /// <b>Must be called on a background thread.</b>
  /// </summary>
  public static List<NetworkResource> EnumerateNetworkResources(
      string? parentParsingPath = null) {

    var result = new List<NetworkResource>();

    if (parentParsingPath == null) {
      // BExplorer-proven path:
      //   SHGetDesktopFolder()                        → IShellFolder (desktop root)
      //   SHGetSpecialFolderLocation(CSIDL_NETWORK)   → PIDL for Network
      //   desktop.BindToObject(networkPidl)            → IShellFolder for Network
      //   EnumObjects with FOLDERS|NONFOLDERS|NETPRINTERSRCH|SHAREABLE
      IShellFolder? netFolder  = null;
      IntPtr        networkPidl = IntPtr.Zero;
      try {
        if (SHGetDesktopFolder(out var desktopObj) == 0) {
          var desktop = (IShellFolder)desktopObj;
          try {
            if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_NETWORK, out networkPidl) == 0
                && networkPidl != IntPtr.Zero) {
              desktop.BindToObject(networkPidl, IntPtr.Zero, IID_IShellFolder, out var netObj);
              netFolder = (IShellFolder)netObj;
            }
          } finally { Marshal.ReleaseComObject(desktop); }
        }
      } catch { }
      finally {
        if (networkPidl != IntPtr.Zero) Marshal.FreeCoTaskMem(networkPidl);
      }

      if (netFolder != null) {
        try { EnumerateNetworkFolderChildren(netFolder, result); }
        finally { Marshal.ReleaseComObject(netFolder); }
      }

    } else {
      // Sub-container: bind via desktop ParseDisplayName, then enumerate children.
      IShellFolder? subFolder  = null;
      IntPtr        childPidl2 = IntPtr.Zero;
      try {
        if (SHGetDesktopFolder(out var desktopObj) == 0) {
          var desktop = (IShellFolder)desktopObj;
          try {
            uint eaten = 0, dAttrs = 0;
            desktop.ParseDisplayName(IntPtr.Zero, IntPtr.Zero,
                parentParsingPath, out eaten, out childPidl2, ref dAttrs);
            if (childPidl2 != IntPtr.Zero) {
              desktop.BindToObject(childPidl2, IntPtr.Zero, IID_IShellFolder, out var subObj);
              subFolder = (IShellFolder)subObj;
            }
          } finally { Marshal.ReleaseComObject(desktop); }
        }
      } catch { }
      finally {
        if (childPidl2 != IntPtr.Zero) Marshal.FreeCoTaskMem(childPidl2);
      }

      if (subFolder != null) {
        try { EnumerateNetworkFolderChildren(subFolder, result); }
        finally { Marshal.ReleaseComObject(subFolder); }
      } else {
        // Last resort for plain UNC server paths: WNet share enumeration.
        result.AddRange(EnumerateServerShares(parentParsingPath));
      }
    }

    return result;
  }

  // Enumerates the immediate children of a shell IShellFolder and converts each to a
  // NetworkResource.  Uses NETPRINTERSRCH|SHAREABLE so printers, UPnP devices, media
  // devices, infrastructure nodes etc. are included — not just computers.
  // Does NOT recurse: Explorer shows workgroups/device-groups as expandable nodes.
  private static void EnumerateNetworkFolderChildren(
      IShellFolder folder, List<NetworkResource> result) {

    const uint enumFlags = SHCONTF_FOLDERS | SHCONTF_NONFOLDERS
                         | SHCONTF_NETPRINTERSRCH | SHCONTF_SHAREABLE
                         | SHCONTF_ENABLE_ASYNC; // items may arrive later via shell notifications

    if (folder.EnumObjects(IntPtr.Zero, enumFlags, out var enumIdList) != 0
        || enumIdList == null) return;
    try {
      while (enumIdList.Next(1, out var childPidl, out _) == 0) {
        try {
          // Parsing name — used as the unique identifier / navigation path.
          var strretParsing = default(STRRET);
          folder.GetDisplayNameOf(childPidl, SHGDN_FORPARSING, out strretParsing);
          string? parseName = StrRetToStr(ref strretParsing, childPidl);
          if (string.IsNullOrEmpty(parseName)) continue;

          // Display name — what the user sees.
          var strretDisplay = default(STRRET);
          folder.GetDisplayNameOf(childPidl, 0, out strretDisplay);
          string? displayName = StrRetToStr(ref strretDisplay, childPidl);
          if (string.IsNullOrEmpty(displayName)) displayName = parseName;

          // Folder flag — containers are expandable (workgroup, device group…).
          uint attrs = SFGAO_FOLDER;
          folder.GetAttributesOf(1, [childPidl], ref attrs);
          bool isFolder = (attrs & SFGAO_FOLDER) != 0;

          string category = GetNetworkItemCategory(folder, childPidl, parseName, isFolder);

          bool isShare = !isFolder
                      && (parseName.StartsWith(@"\\", StringComparison.Ordinal)
                          || parseName.StartsWith("//", StringComparison.Ordinal));

          result.Add(new NetworkResource {
            RemoteName  = parseName,
            DisplayName = displayName,
            Category    = category,
            IsContainer = isFolder,
            IsShare     = isShare,
          });
        } catch { } finally { Marshal.FreeCoTaskMem(childPidl); }
      }
    } finally { Marshal.ReleaseComObject(enumIdList); }
  }

  /// <summary>
  /// Enumerates disk shares directly under a server using WNetEnumResource.
  /// Used when the user expands a server node that has no shell sub-namespace
  /// (i.e. its children are UNC shares, not further shell containers).
  /// <b>Must be called on a background thread.</b>
  /// </summary>
  public static List<NetworkResource> EnumerateServerShares(string serverRemoteName) {
    var    result = new List<NetworkResource>();
    IntPtr hEnum  = IntPtr.Zero;

    var nr = new NETRESOURCE {
      dwScope      = 0x00000002, // RESOURCE_GLOBALNET
      dwType       = RESOURCETYPE_DISK,
      dwUsage      = RESOURCEUSAGE_CONTAINER,
      lpRemoteName = serverRemoteName,
    };
    int err = WNetOpenEnum(SCOPE_GLOBALNET, RESOURCETYPE_DISK, 0, ref nr, out hEnum);
    if (err != NET_NO_ERROR || hEnum == IntPtr.Zero) return result;

    try {
      const uint bufSize = 16384;
      IntPtr     buf     = Marshal.AllocHGlobal((int)bufSize);
      try {
        while (true) {
          uint count   = 0xFFFFFFFF;
          uint bufUsed = bufSize;
          err = WNetEnumResource(hEnum, ref count, buf, ref bufUsed);
          if (err == ERROR_NO_MORE_ITEMS) break;
          if (err != NET_NO_ERROR)        break;
          if (count == 0)                 break;

          int    stride = Marshal.SizeOf<NETRESOURCE>();
          IntPtr ptr    = buf;
          for (uint i = 0; i < count; i++, ptr = IntPtr.Add(ptr, stride)) {
            var r = Marshal.PtrToStructure<NETRESOURCE>(ptr);
            if (string.IsNullOrEmpty(r.lpRemoteName)) continue;
            var baseName = r.lpRemoteName.TrimStart('\\');
            var disp     = !string.IsNullOrWhiteSpace(r.lpComment)
                ? $"{baseName} ({r.lpComment})" : baseName;
            result.Add(new NetworkResource {
              RemoteName  = r.lpRemoteName,
              DisplayName = disp,
              Category    = "Network Share",
              IsContainer = false,
              IsShare     = true,
            });
          }
        }
      } finally { Marshal.FreeHGlobal(buf); }
    } finally { WNetCloseEnum(hEnum); }
    return result;
  }

  // ── IFileOperation helper ─────────────────────────────────────────────────

  /// <summary>
  /// Performs a shell copy or move using <c>IFileOperation</c>, which shows
  /// the standard Windows Explorer progress / conflict dialog.
  /// </summary>
  /// <param name="sourcePaths">Full paths of items to copy or move.</param>
  /// <param name="destFolderPath">Full path of the destination folder.</param>
  /// <param name="move"><see langword="true"/> to move; <see langword="false"/> to copy.</param>
  /// <param name="hwndOwner">Owner window handle for the progress dialog.</param>
  /// <returns>
  /// A tuple of (<c>removedPaths</c>, <c>addedPaths</c>) suitable for
  /// passing to the caller's surgical view-update logic.
  /// </returns>
  public static Task<(List<string> Removed, List<string> Added)> ShellFileOperationAsync(
      IReadOnlyList<string> sourcePaths,
      string destFolderPath,
      bool move,
      IntPtr hwndOwner = default,
      bool useDarkMode = false) {
    // IFileOperation must run on an STA thread; Task.Run on the thread-pool
    // is always MTA, so we spin up a dedicated STA thread.
    var tcs = new TaskCompletionSource<(List<string>, List<string>)>();

    var thread = new System.Threading.Thread(() => {
      var removed = new List<string>();
      var added = new List<string>();
      IFileOperation? fileOp = null;
      uint cookie = 0;
      var sink = new FileOperationSink(removed, added, move);

      try {
        int hr = CoCreateInstance(
            CLSID_FileOperation, IntPtr.Zero,
            /*CLSCTX_INPROC_SERVER=*/1,
            IID_IFileOperation,
            out object opObj);
        Marshal.ThrowExceptionForHR(hr);
        fileOp = (IFileOperation)opObj;

        if (hwndOwner != IntPtr.Zero)
          fileOp.SetOwnerWindow(hwndOwner);

        // Allow undo and auto-rename on collision (matches Explorer behaviour).
        fileOp.SetOperationFlags(FOF_NOCONFIRMMKDIR | FOFX_ADDUNDORECORD);

        fileOp.Advise(sink, out cookie);

        SHCreateItemFromParsingNameOp(
            destFolderPath, IntPtr.Zero, IID_IShellItemOp, out IShellItemOp destItem);

        foreach (var src in sourcePaths) {
          SHCreateItemFromParsingNameOp(
              src, IntPtr.Zero, IID_IShellItemOp, out IShellItemOp srcItem);

          if (move)
            fileOp.MoveItem(srcItem, destItem, null, null);
          else
            fileOp.CopyItem(srcItem, destItem, null, null);
        }

        // Push the app's dark/light preference into uxtheme so the shell
        // progress/conflict dialog renders with the correct chrome.
        // 2 = ForceDark, 3 = ForceLight
        try {
          SetPreferredAppMode(useDarkMode ? 2 : 3);
          FlushMenuThemes();
        } catch { /* ordinals absent on very old Windows builds */ }

        try {
          hr = fileOp.PerformOperations();
          Marshal.ThrowExceptionForHR(hr);
        } finally {
          // Restore AllowDark (1) so the rest of the process is unaffected.
          try { SetPreferredAppMode(1); FlushMenuThemes(); } catch { }
        }

        tcs.SetResult((removed, added));
      } catch (Exception ex) {
        tcs.SetException(ex);
      } finally {
        if (fileOp is not null && cookie != 0)
          fileOp.Unadvise(cookie);
      }
    });

    thread.SetApartmentState(System.Threading.ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();

    return tcs.Task;
  }

  // ── Progress sink ─────────────────────────────────────────────────────────

  /// <summary>
  /// Minimal <see cref="IFileOperationProgressSink"/> that records which
  /// items were successfully moved / copied so the caller can update its
  /// view surgically.
  /// </summary>
  private sealed class FileOperationSink : IFileOperationProgressSink {
    private readonly List<string> _removed;
    private readonly List<string> _added;
    private readonly bool _move;

    internal FileOperationSink(List<string> removed, List<string> added, bool move) {
      _removed = removed;
      _added = added;
      _move = move;
    }

    private static string? GetPath(IShellItemOp? item) {
      if (item is null)
        return null;
      try {
        // SIGDN_FILESYSPATH = 0x80058000
        item.GetDisplayName(0x80058000u, out string path);
        return path;
      } catch { return null; }
    }

    public void StartOperations() { }
    public void FinishOperations(int hrResult) { }

    public void PostMoveItem(uint dwFlags, IShellItemOp psiItem,
        IShellItemOp psiDestinationFolder, string pszNewName,
        int hrMove, IShellItemOp psiNewlyCreated) {
      if (hrMove < 0)
        return;
      var src = GetPath(psiItem);
      var dest = GetPath(psiNewlyCreated);
      if (src is not null)
        _removed.Add(src);
      if (dest is not null)
        _added.Add(dest);
    }

    public void PostCopyItem(uint dwFlags, IShellItemOp psiItem,
        IShellItemOp psiDestinationFolder, string pszNewName,
        int hrCopy, IShellItemOp psiNewlyCreated) {
      if (hrCopy < 0)
        return;
      var dest = GetPath(psiNewlyCreated);
      if (dest is not null)
        _added.Add(dest);
    }

    // No-op stubs for the remaining sink methods
    public void PreRenameItem(uint dwFlags, IShellItemOp psiItem, string pszNewName) { }
    public void PostRenameItem(uint dwFlags, IShellItemOp psiItem, string pszNewName,
        int hrRename, IShellItemOp psiNewlyCreated) { }
    public void PreMoveItem(uint dwFlags, IShellItemOp psiItem,
        IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PreCopyItem(uint dwFlags, IShellItemOp psiItem,
        IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PreDeleteItem(uint dwFlags, IShellItemOp psiItem) { }
    public void PostDeleteItem(uint dwFlags, IShellItemOp psiItem, int hrDelete,
        IShellItemOp psiNewlyCreated) { }
    public void PreNewItem(uint dwFlags, IShellItemOp psiDestinationFolder,
        string pszNewName) { }
    public void PostNewItem(uint dwFlags, IShellItemOp psiDestinationFolder,
        string pszNewName, string pszTemplateName, uint dwFileAttributes,
        int hrNew, IShellItemOp psiNewItem) { }
    public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar) { }
    public void ResetTimer() { }
    public void PauseTimer() { }
    public void ResumeTimer() { }
  }

  // ── Rename sink ───────────────────────────────────────────────────────────

  private sealed class RenameSink : IFileOperationProgressSink {
    internal string? NewPath { get; private set; }

    private static string? GetPath(IShellItemOp? item) {
      if (item is null) return null;
      try { item.GetDisplayName(0x80058000u, out string path); return path; } catch { return null; }
    }

    public void PostRenameItem(uint dwFlags, IShellItemOp psiItem, string pszNewName,
        int hrRename, IShellItemOp psiNewlyCreated) {
      if (hrRename >= 0)
        NewPath = GetPath(psiNewlyCreated);
    }

    public void StartOperations() { }
    public void FinishOperations(int hrResult) { }
    public void PreRenameItem(uint dwFlags, IShellItemOp psiItem, string pszNewName) { }
    public void PreMoveItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PostMoveItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName, int hrMove, IShellItemOp psiNewlyCreated) { }
    public void PreCopyItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PostCopyItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName, int hrCopy, IShellItemOp psiNewlyCreated) { }
    public void PreDeleteItem(uint dwFlags, IShellItemOp psiItem) { }
    public void PostDeleteItem(uint dwFlags, IShellItemOp psiItem, int hrDelete, IShellItemOp psiNewlyCreated) { }
    public void PreNewItem(uint dwFlags, IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PostNewItem(uint dwFlags, IShellItemOp psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItemOp psiNewItem) { }
    public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar) { }
    public void ResetTimer() { }
    public void PauseTimer() { }
    public void ResumeTimer() { }
  }

  // ── Shell rename helper ────────────────────────────────────────────────────

  /// <summary>
  /// Renames a single file or folder via <c>IFileOperation</c>, which
  /// adds an undo record in Explorer and shows a conflict dialog if needed.
  /// </summary>
  /// <returns>The new full path on success, or <see langword="null"/> if the
  /// operation was cancelled or failed.</returns>
  public static Task<string?> ShellRenameAsync(
      string sourcePath, string newName,
      IntPtr hwndOwner = default) {
    var tcs = new TaskCompletionSource<string?>();

    var thread = new System.Threading.Thread(() => {
      IFileOperation? fileOp = null;
      uint cookie = 0;
      var sink = new RenameSink();
      try {
        int hr = CoCreateInstance(
            CLSID_FileOperation, IntPtr.Zero, 1,
            IID_IFileOperation, out object opObj);
        Marshal.ThrowExceptionForHR(hr);
        fileOp = (IFileOperation)opObj;

        if (hwndOwner != IntPtr.Zero)
          fileOp.SetOwnerWindow(hwndOwner);

        // Silent rename — no progress dialog, no confirmation prompts.
        // FOFX_ADDUNDORECORD still adds an undo entry to Explorer's undo stack.
        fileOp.SetOperationFlags(FOF_NO_UI | FOFX_ADDUNDORECORD);
        fileOp.Advise(sink, out cookie);

        SHCreateItemFromParsingNameOp(
            sourcePath, IntPtr.Zero, IID_IShellItemOp, out IShellItemOp srcItem);

        fileOp.RenameItem(srcItem, newName, null);

        hr = fileOp.PerformOperations();
        Marshal.ThrowExceptionForHR(hr);

        tcs.SetResult(sink.NewPath);
      } catch (Exception ex) {
        tcs.SetException(ex);
      } finally {
        if (fileOp is not null && cookie != 0)
          fileOp.Unadvise(cookie);
      }
    });

    thread.SetApartmentState(System.Threading.ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    return tcs.Task;
  }

  // ── Shell delete (to Recycle Bin) ─────────────────────────────────────────

  private const uint FOF_ALLOWUNDO    = 0x0040;

  /// <summary>
  /// Deletes the given paths to the Recycle Bin via <c>IFileOperation</c>.
  /// Shows the standard shell progress/confirmation UI.
  /// </summary>
  public static Task ShellDeleteAsync(
      IReadOnlyList<string> paths,
      IntPtr hwndOwner = default,
      bool useDarkMode = false,
      bool permanent = false) {
    var tcs = new TaskCompletionSource();

    var thread = new System.Threading.Thread(() => {
      IFileOperation? fileOp = null;
      uint cookie = 0;
      var sink = new DeleteSink();
      try {
        int hr = CoCreateInstance(CLSID_FileOperation, IntPtr.Zero, 1,
            IID_IFileOperation, out object opObj);
        Marshal.ThrowExceptionForHR(hr);
        fileOp = (IFileOperation)opObj;

        if (hwndOwner != IntPtr.Zero)
          fileOp.SetOwnerWindow(hwndOwner);

        fileOp.SetOperationFlags(permanent
            ? FOF_NOCONFIRMATION
            : FOF_ALLOWUNDO | FOFX_ADDUNDORECORD);
        fileOp.Advise(sink, out cookie);

        int itemsQueued = 0;
        foreach (var path in paths) {
          // Skip items that no longer exist — they may have been removed by a
          // file-system watcher or a previous operation before we got here.
          if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path))
            continue;
          try {
            SHCreateItemFromParsingNameOp(path, IntPtr.Zero, IID_IShellItemOp, out IShellItemOp srcItem);
            fileOp.DeleteItem(srcItem, null);
            itemsQueued++;
          } catch {
            // Path became unavailable between the existence check and binding —
            // skip it silently so IFileOperation doesn't surface a "not found" dialog.
          }
        }

        if (itemsQueued == 0) {
          tcs.SetResult();
          return;
        }

        try {
          SetPreferredAppMode(useDarkMode ? 2 : 3);
          FlushMenuThemes();
        } catch { }

        try {
          hr = fileOp.PerformOperations();
          Marshal.ThrowExceptionForHR(hr);
        } finally {
          try { SetPreferredAppMode(1); FlushMenuThemes(); } catch { }
        }

        tcs.SetResult();
      } catch (Exception ex) {
        tcs.SetException(ex);
      } finally {
        if (fileOp is not null && cookie != 0)
          fileOp.Unadvise(cookie);
      }
    });

    thread.SetApartmentState(System.Threading.ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    return tcs.Task;
  }

  private sealed class DeleteSink : IFileOperationProgressSink {
    public void StartOperations() { }
    public void FinishOperations(int hrResult) { }
    public void PreRenameItem(uint dwFlags, IShellItemOp psiItem, string pszNewName) { }
    public void PostRenameItem(uint dwFlags, IShellItemOp psiItem, string pszNewName, int hrRename, IShellItemOp psiNewlyCreated) { }
    public void PreMoveItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PostMoveItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName, int hrMove, IShellItemOp psiNewlyCreated) { }
    public void PreCopyItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PostCopyItem(uint dwFlags, IShellItemOp psiItem, IShellItemOp psiDestinationFolder, string pszNewName, int hrCopy, IShellItemOp psiNewlyCreated) { }
    public void PreDeleteItem(uint dwFlags, IShellItemOp psiItem) { }
    public void PostDeleteItem(uint dwFlags, IShellItemOp psiItem, int hrDelete, IShellItemOp psiNewlyCreated) { }
    public void PreNewItem(uint dwFlags, IShellItemOp psiDestinationFolder, string pszNewName) { }
    public void PostNewItem(uint dwFlags, IShellItemOp psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItemOp psiNewItem) { }
    public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar) { }
    public void ResetTimer() { }
    public void PauseTimer() { }
    public void ResumeTimer() { }
  }

  // ── Shell Properties dialog ───────────────────────────────────────────────

  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFOW lpExecInfo);

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct SHELLEXECUTEINFOW {
    public int    cbSize;
    public uint   fMask;
    public IntPtr hwnd;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
    [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
    public int    nShow;
    public IntPtr hInstApp;
    public IntPtr lpIDList;
    [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
    public IntPtr hkeyClass;
    public uint   dwHotKey;
    public IntPtr hIconOrMonitor;
    public IntPtr hProcess;
  }

  /// <summary>Shows the Windows shell Properties dialog for <paramref name="path"/>.</summary>
  public static void ShowShellProperties(string path, IntPtr hwnd = default) {
    var sei = new SHELLEXECUTEINFOW {
      cbSize = Marshal.SizeOf<SHELLEXECUTEINFOW>(),
      fMask  = 0x0000000C, // SEE_MASK_INVOKEIDLIST
      hwnd   = hwnd,
      lpVerb = "properties",
      lpFile = path,
      nShow  = 5, // SW_SHOW
    };
    ShellExecuteExW(ref sei);
  }

  // ── Folder icon helpers ───────────────────────────────────────────────────

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool WritePrivateProfileStringW(
      string lpAppName, string? lpKeyName, string? lpString, string lpFileName);

  // Overload used to flush the INI cache: all three string args are null.
  [DllImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool WritePrivateProfileStringFlushW(
      string? lpAppName, string? lpKeyName, string? lpString, string? lpFileName);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern uint GetPrivateProfileStringW(
      string lpAppName, string lpKeyName, string lpDefault,
      System.Text.StringBuilder lpReturnedString, uint nSize, string lpFileName);

  [DllImport("shell32.dll")]
  private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

  // Read-capable variant of SHFOLDERCUSTOMSETTINGS: pszIconFile is an IntPtr
  // so the caller allocates the buffer and the shell fills it in.  The write
  // struct in Shell32.cs uses `string` which the marshaller can only pass IN,
  // not receive back — hence this separate private definition.
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  private struct SHFCS_READ {
    public uint   dwSize;
    public uint   dwMask;
    public IntPtr pvid;
    public IntPtr pszWebViewTemplate;
    public uint   cchWebViewTemplate;
    public IntPtr pszWebViewTemplateVersion;
    public IntPtr pszInfoTip;
    public uint   cchInfoTip;
    public IntPtr pclsid;
    public uint   dwFlags;
    public IntPtr pszIconFile;   // caller-allocated output buffer
    public uint   cchIconFile;   // size of that buffer in WCHARs
    public int    iIconIndex;
    public IntPtr pszLogo;
    public uint   cchLogo;
  }

  [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetSetFolderCustomSettings")]
  private static extern HResult SHGetSetFolderCustomSettingsRead(
      ref SHFCS_READ pfcs, string pszPath, uint dwReadWrite);

  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  private static extern int SHFormatDrive(IntPtr hwnd, uint drive, uint fmtID, uint options);

  private const uint FILE_ATTRIBUTE_READONLY  = 0x01;
  private const uint FILE_ATTRIBUTE_SYSTEM    = 0x04;

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern bool SetFileAttributesW(string lpFileName, uint dwFileAttributes);

  /// <summary>
  /// Sets a custom icon for <paramref name="folderPath"/> using
  /// <c>SHGetSetFolderCustomSettings</c> — the same API used by Explorer itself.
  /// <paramref name="iconFile"/> can be a .ico, .exe, or .dll path.
  /// <paramref name="iconIndex"/> is the 0-based resource index.
  /// </summary>
  public static void SetFolderIcon(string folderPath, string iconFile, int iconIndex) {
    if (!Directory.Exists(folderPath)) return;

    var fcs = new Shell32.LPSHFOLDERCUSTOMSETTINGS {
      dwMask      = Shell32.FCSM_ICONFILE,
      pszIconFile = iconFile.Replace(@"\\", @"\"),
      cchIconFile = 0,
      iIconIndex  = iconIndex,
    };
    fcs.dwSize = (uint)Marshal.SizeOf(fcs);

    var hr = Shell32.SHGetSetFolderCustomSettings(ref fcs,
        folderPath.Replace(@"\\", @"\"), Shell32.FCS_FORCEWRITE);

    if (hr == HResult.S_OK)
      UpdateIconCacheForFolder(folderPath.Replace(@"\\", @"\"));
  }

  /// <summary>Removes the custom icon from <paramref name="folderPath"/> (restores default shell icon).</summary>
  public static void RestoreFolderIcon(string folderPath) {
    if (!Directory.Exists(folderPath)) return;

    // Mirrors Better Explorer's ClearFolderIcon exactly:
    // pszIconFile = null + iIconIndex = 0 + FCS_FORCEWRITE tells the shell
    // to remove the IconFile entry entirely rather than writing a blank value.
    var fcs = new Shell32.LPSHFOLDERCUSTOMSETTINGS {
      dwMask      = Shell32.FCSM_ICONFILE,
      pszIconFile = null,
      cchIconFile = 0,
      iIconIndex  = 0,
    };
    fcs.dwSize = (uint)Marshal.SizeOf(fcs);

    var hr = Shell32.SHGetSetFolderCustomSettings(ref fcs,
        folderPath.Replace(@"\\", @"\"), Shell32.FCS_FORCEWRITE);

    if (hr == HResult.S_OK)
      UpdateIconCacheForFolder(folderPath.Replace(@"\\", @"\"));
  }

  /// <summary>
  /// Flushes the shell icon cache for <paramref name="folderPath"/> so the new
  /// icon is reflected immediately — mirrors Better Explorer's implementation.
  /// </summary>
  private static void UpdateIconCacheForFolder(string folderPath) {
    // Read back the image-list index the shell assigned to this folder's icon.
    var fcsRead = new Shell32.LPSHFOLDERCUSTOMSETTINGS {
      dwMask      = Shell32.FCSM_ICONFILE,
      pszIconFile = string.Empty,
      cchIconFile = 0,
      iIconIndex  = 0,
    };
    fcsRead.dwSize = (uint)Marshal.SizeOf(fcsRead);

    Shell32.SHGetSetFolderCustomSettings(ref fcsRead, folderPath, Shell32.FCS_READ);

    // Invalidate that system-image-list slot so every window redraws with the new icon.
    Shell32.SHUpdateImage(fcsRead.pszIconFile ?? string.Empty, fcsRead.iIconIndex, 0, -1);

    // Send only a targeted SHCNE_UPDATEITEM for the specific folder.
    // Do NOT send SHCNE_ASSOCCHANGED — it has no path and causes every shell
    // change listener to trigger a full directory reload, which clears the
    // current selection and collapses the contextual toolbar.
    var pszFolder = Marshal.StringToHGlobalUni(folderPath);
    try {
      SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */,
          0x0005 /* SHCNF_PATHW | SHCNF_FLUSH */, pszFolder, IntPtr.Zero);
    } finally {
      Marshal.FreeHGlobal(pszFolder);
    }
  }

  /// <summary>
  /// Sends <c>SHCNE_UPDATEDIR</c> for <paramref name="dirPath"/> so the shell
  /// invalidates its thumbnail-cache entries for all items inside that directory.
  /// Safe to call for a single-folder restore — does not trigger a full list reload.
  /// </summary>
  public static void NotifyShellUpdateDir(string dirPath) {
    var ptr = Marshal.StringToHGlobalUni(dirPath);
    try {
      SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */,
          0x0005 /* SHCNF_PATHW | SHCNF_FLUSH */, ptr, IntPtr.Zero);
    } finally {
      Marshal.FreeHGlobal(ptr);
    }
  }

  /// <summary>
  /// Returns <see langword="true"/> if <paramref name="folderPath"/> has a custom icon
  /// set via <c>SHGetSetFolderCustomSettings</c>.
  /// </summary>
  /// <summary>
  /// Returns <see langword="true"/> if <paramref name="folderPath"/> has a
  /// custom icon recorded in its desktop.ini — handles both the
  /// <c>IconFile=</c> key written by <c>SHGetSetFolderCustomSettings</c> and
  /// the <c>IconResource=</c> key written by Explorer's Customize tab.
  /// </summary>
  // ── desktop.ini helpers ──────────────────────────────────────────────────

  /// <summary>
  /// Reads a single key from a .ini-style file, handling both UTF-16 LE (shell-
  /// written) and ANSI/UTF-8 encodings.  Returns <see langword="null"/> if the
  /// section or key is not found, or on any I/O error.
  /// </summary>
  private static string? ReadIniValue(string iniPath, string section, string key) {
    try {
      var lines = File.ReadAllLines(iniPath);
      var sectionHeader = $"[{section}]";
      var keyPrefix     = $"{key}=";
      bool inSection    = false;
      foreach (var raw in lines) {
        var line = raw.Trim();
        if (line.StartsWith("[", StringComparison.Ordinal)) {
          inSection = line.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase);
          continue;
        }
        if (inSection && line.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
          return line[keyPrefix.Length..].Trim();
      }
    } catch { }
    return null;
  }

  // Buffer size (in WCHARs) used when asking SHGetSetFolderCustomSettings to
  // fill in pszIconFile.  MAX_PATH is sufficient; the shell will truncate to fit.
  private const uint IconFileBufLen = 260;

  /// <summary>
  /// Returns <see langword="true"/> if <paramref name="folderPath"/> has a custom
  /// folder icon set via <c>SHGetSetFolderCustomSettings</c>.
  /// </summary>
  public static bool HasCustomFolderIcon(string folderPath) {
    if (!Directory.Exists(folderPath)) return false;
    // Allocate an unmanaged WCHAR buffer for the shell to write into.
    var buf = Marshal.AllocHGlobal((int)(IconFileBufLen * 2));
    try {
      Marshal.WriteInt16(buf, 0); // zero-terminate so empty == no icon
      var fcs = new SHFCS_READ {
        dwMask      = Shell32.FCSM_ICONFILE,
        pszIconFile = buf,
        cchIconFile = IconFileBufLen,
      };
      fcs.dwSize = (uint)Marshal.SizeOf(fcs);
      var hr = SHGetSetFolderCustomSettingsRead(ref fcs, folderPath, Shell32.FCS_READ);
      if (hr != HResult.S_OK) return false;
      var result = Marshal.PtrToStringUni(buf);
      return !string.IsNullOrWhiteSpace(result);
    } finally {
      Marshal.FreeHGlobal(buf);
    }
  }

  public static string? GetFolderIconResource(string folderPath) {
    if (!Directory.Exists(folderPath)) return null;
    var buf = Marshal.AllocHGlobal((int)(IconFileBufLen * 2));
    try {
      Marshal.WriteInt16(buf, 0);
      var fcs = new SHFCS_READ {
        dwMask      = Shell32.FCSM_ICONFILE,
        pszIconFile = buf,
        cchIconFile = IconFileBufLen,
      };
      fcs.dwSize = (uint)Marshal.SizeOf(fcs);
      var hr = SHGetSetFolderCustomSettingsRead(ref fcs, folderPath, Shell32.FCS_READ);
      if (hr != HResult.S_OK) return null;
      var iconFile = Marshal.PtrToStringUni(buf);
      if (!string.IsNullOrWhiteSpace(iconFile))
        return $"{iconFile},{fcs.iIconIndex}";
      // Fallback: Explorer "Customize" tab writes IconResource= to desktop.ini
      var iniPath = Path.Combine(folderPath, "desktop.ini");
      var res = ReadIniValue(iniPath, ".ShellClassInfo", "IconResource");
      return string.IsNullOrWhiteSpace(res) ? null : res;
    } finally {
      Marshal.FreeHGlobal(buf);
    }
  }

  /// <summary>Returns <see langword="true"/> if <paramref name="path"/> is a drive root (e.g. "C:\").</summary>
  public static bool IsDriveRoot(string path) {
    if (string.IsNullOrEmpty(path)) return false;
    // Normalise: strip trailing backslash for Path.GetPathRoot comparison
    var root = Path.GetPathRoot(path);
    return !string.IsNullOrEmpty(root) &&
           string.Equals(Path.TrimEndingDirectorySeparator(path),
                         Path.TrimEndingDirectorySeparator(root),
                         StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>Invokes the Windows Format dialog for the given drive letter.</summary>
  public static void FormatDrive(char driveLetter, IntPtr hwnd = default) {
    uint driveIndex = (uint)(char.ToUpperInvariant(driveLetter) - 'A');
    SHFormatDrive(hwnd, driveIndex, 0xFFFF /* SHFMT_ID_DEFAULT */, 0);
  }

  /// <summary>Launches Disk Cleanup (cleanmgr.exe) for the given drive letter.</summary>
  public static void OpenDiskCleanup(char driveLetter) {
    var letter = char.ToUpperInvariant(driveLetter);
    var psi = new System.Diagnostics.ProcessStartInfo("cleanmgr.exe",
        $"/d {letter}:")
    {
        UseShellExecute = true,
    };
    System.Diagnostics.Process.Start(psi);
  }

  // ── Icon extraction (for FolderIconPickerDialog) ──────────────────────────

  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  private static extern int ExtractIconExW(
      string lpszFile, int nIconIndex,
      [Out] IntPtr[]? phiconLarge, [Out] IntPtr[]? phiconSmall, uint nIcons);

  /// <summary>Returns the number of icon resources in <paramref name="filePath"/>.</summary>
  public static int ExtractIconCount(string filePath) {
    try { return ExtractIconExW(filePath, -1, null, null, 0); }
    catch { return 0; }
  }

  /// <summary>
  /// Renders the icon at <paramref name="index"/> in <paramref name="filePath"/> to a
  /// 32-bit HBITMAP of the given <paramref name="size"/>. Returns <see cref="IntPtr.Zero"/>
  /// on failure. Caller must call <see cref="DeleteObject"/> when done.
  /// </summary>
  public static IntPtr ExtractIconHBitmap(string filePath, int index, int size) {
    var large = new IntPtr[1];
    var small = new IntPtr[1];
    try {
      int n = ExtractIconExW(filePath, index, large, small, 1);
      if (n <= 0) return IntPtr.Zero;
      var hIcon = size > 16 ? large[0] : small[0];
      if (hIcon == IntPtr.Zero) return IntPtr.Zero;

      var bmi = new BITMAPINFOHEADER {
        biSize        = Marshal.SizeOf<BITMAPINFOHEADER>(),
        biWidth       = size,
        biHeight      = -size,
        biPlanes      = 1,
        biBitCount    = 32,
        biCompression = 0,
      };
      var hdc = CreateCompatibleDC(IntPtr.Zero);
      var hbm = CreateDIBSection(hdc, ref bmi, 0, out _, IntPtr.Zero, 0);
      var hOld = SelectObject(hdc, hbm);
      DrawIconEx(hdc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, DI_NORMAL);
      SelectObject(hdc, hOld);
      DeleteDC(hdc);
      return hbm;
    } finally {
      if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
      if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
    }
  }
  // ── Picture helpers ───────────────────────────────────────────────────────

  private const uint SPI_SETDESKWALLPAPER = 0x0014;
  private const uint SPIF_UPDATEINIFILE   = 0x0001;
  private const uint SPIF_SENDCHANGE      = 0x0002;

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam,
      string pvParam, uint fWinIni);

  /// <summary>Sets the desktop wallpaper to the given image file.</summary>
  public static void SetWallpaper(string imagePath) =>
      SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, imagePath,
          SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

  /// <summary>
  /// Rotates the image file at <paramref name="imagePath"/> in-place by
  /// <paramref name="rotation"/> and re-encodes it in the same format.
  /// </summary>
  public static async Task RotateImageAsync(string imagePath, BitmapRotation rotation) {
    var file = await StorageFile.GetFileFromPathAsync(imagePath);

    Guid    codecId;
    byte[]  pixels;
    uint    rotatedWidth, rotatedHeight;

    using (var inStream = await file.OpenReadAsync()) {
      var decoder = await BitmapDecoder.CreateAsync(inStream);
      codecId = decoder.DecoderInformation.CodecId;

      bool swap = rotation == BitmapRotation.Clockwise90Degrees ||
                  rotation == BitmapRotation.Clockwise270Degrees;
      rotatedWidth  = swap ? decoder.PixelHeight : decoder.PixelWidth;
      rotatedHeight = swap ? decoder.PixelWidth  : decoder.PixelHeight;

      var pd = await decoder.GetPixelDataAsync(
          BitmapPixelFormat.Bgra8,
          BitmapAlphaMode.Premultiplied,
          new BitmapTransform { Rotation = rotation },
          ExifOrientationMode.IgnoreExifOrientation,
          ColorManagementMode.DoNotColorManage);
      pixels = pd.DetachPixelData();
    }

    using var outStream = await file.OpenAsync(FileAccessMode.ReadWrite);
    outStream.Seek(0);
    outStream.Size = 0;

    Guid encoderId = codecId == BitmapDecoder.JpegDecoderId  ? BitmapEncoder.JpegEncoderId
                   : codecId == BitmapDecoder.PngDecoderId   ? BitmapEncoder.PngEncoderId
                   : codecId == BitmapDecoder.BmpDecoderId   ? BitmapEncoder.BmpEncoderId
                   : codecId == BitmapDecoder.GifDecoderId   ? BitmapEncoder.GifEncoderId
                   : codecId == BitmapDecoder.TiffDecoderId  ? BitmapEncoder.TiffEncoderId
                   : BitmapEncoder.PngEncoderId;

    var encoder = await BitmapEncoder.CreateAsync(encoderId, outStream);
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
        rotatedWidth, rotatedHeight, 96, 96, pixels);
    await encoder.FlushAsync();
  }

  // ── WSL / Linux ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Returns the display names of all installed WSL distributions by reading
  /// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss</c>.
  /// Returns an empty list when WSL is not installed or no distros are registered.
  /// </summary>
  public static List<string> EnumerateWslDistributions() {
    var result = new List<string>();
    try {
      using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
          @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lxss");
      if (lxss == null) return result;

      foreach (var subName in lxss.GetSubKeyNames()) {
        using var sub = lxss.OpenSubKey(subName);
        if (sub?.GetValue("DistributionName") is string name && !string.IsNullOrEmpty(name))
          result.Add(name);
      }
    }
    catch { /* registry not accessible */ }
    return result;
  }

}

public struct PROPERTYKEY {
  public Guid fmtid;
  public int pid;
}

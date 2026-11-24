Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!ifndef APPDIR
  !error "APPDIR must point to the self-contained publish directory"
!endif
!ifndef VERSION
  !error "VERSION is required"
!endif
!ifndef OUTPUT
  !error "OUTPUT is required"
!endif
Name "分流助手 ${VERSION}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\SplitHelper"
RequestExecutionLevel user
SetCompressor /SOLID lzma
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "分流助手"
VIAddVersionKey "FileDescription" "分流助手 Windows 桌面端安装程序"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright (c) SplitHelper contributors"
!ifdef ICONFILE
  Icon "${ICONFILE}"
  UninstallIcon "${ICONFILE}"
!endif
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\SplitHelper.exe"
!define MUI_FINISHPAGE_RUN_TEXT "打开分流助手"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  SetShellVarContext current
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONEXCLAMATION "本安装包需要 64 位 Windows。"
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONEXCLAMATION "本安装包需要 Windows 10 或更新版本。"
    Abort
  ${EndIf}
  SetRegView 64
  IfFileExists "$INSTDIR\SplitHelper.exe" 0 ready
    ClearErrors
    ExecWait '"$INSTDIR\SplitHelper.exe" --prepare-update' $0
    IfErrors update_failed
    StrCmp $0 0 ready update_failed
  update_failed:
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先从分流助手托盘退出并完成代理还原，再运行安装包。"
    Abort
  ready:
FunctionEnd

Section "安装"
  SetOutPath "$INSTDIR"
  File /r "${APPDIR}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\分流助手"
  CreateShortcut "$SMPROGRAMS\分流助手\分流助手.lnk" "$INSTDIR\SplitHelper.exe"
  CreateShortcut "$SMPROGRAMS\分流助手\卸载.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper" "DisplayName" "分流助手"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper" "DisplayIcon" "$INSTDIR\SplitHelper.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  IfFileExists "$INSTDIR\SplitHelper.exe" 0 uninstall_failed
  IfFileExists "$INSTDIR\build-info.json" 0 uninstall_failed
  IfFileExists "$INSTDIR\components\direct-helper\manifest.json" 0 uninstall_failed
  ClearErrors
  ExecWait '"$INSTDIR\SplitHelper.exe" --prepare-uninstall' $0
  IfErrors uninstall_failed
  StrCmp $0 0 uninstall_ready uninstall_failed
  uninstall_failed:
    MessageBox MB_OK|MB_ICONEXCLAMATION "请先退出分流助手和 Clash GUI，并完成代理还原，再重试卸载。未删除程序文件。"
    Abort
  uninstall_ready:
  Delete "$SMPROGRAMS\分流助手\分流助手.lnk"
  Delete "$SMPROGRAMS\分流助手\卸载.lnk"
  RMDir "$SMPROGRAMS\分流助手"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\SplitHelper"
  Delete "$INSTDIR\SplitHelper.exe"
  Delete "$INSTDIR\SplitHelper.pdb"
  Delete "$INSTDIR\build-info.json"
  RMDir /r "$INSTDIR\components"
  RMDir /r "$INSTDIR\licenses"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd

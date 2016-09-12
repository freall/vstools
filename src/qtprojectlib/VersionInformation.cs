/****************************************************************************
**
** Copyright (C) 2016 The Qt Company Ltd.
** Contact: https://www.qt.io/licensing/
**
** This file is part of the Qt VS Tools.
**
** $QT_BEGIN_LICENSE:GPL-EXCEPT$
** Commercial License Usage
** Licensees holding valid commercial Qt licenses may use this file in
** accordance with the commercial license agreement provided with the
** Software or, alternatively, in accordance with the terms contained in
** a written agreement between you and The Qt Company. For licensing terms
** and conditions see https://www.qt.io/terms-conditions. For further
** information use the contact form at https://www.qt.io/contact-us.
**
** GNU General Public License Usage
** Alternatively, this file may be used under the terms of the GNU
** General Public License version 3 as published by the Free Software
** Foundation with exceptions as appearing in the file LICENSE.GPL3-EXCEPT
** included in the packaging of this file. Please review the following
** information to ensure the GNU General Public License requirements will
** be met: https://www.gnu.org/licenses/gpl-3.0.html.
**
** $QT_END_LICENSE$
**
****************************************************************************/

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace QtProjectLib
{
    public class VersionInformation
    {
        //fields
        public string qtDir;
        public uint qtMajor; // X in version x.y.z
        public uint qtMinor; // Y in version x.y.z
        public uint qtPatch; // Z in version x.y.z
        public bool qt5Version = true;
        private QtConfig qtConfig;
        private QMakeConf qmakeConf;
        private string vsPlatformName;

        public VersionInformation(string qtDirIn)
        {
            if (qtDirIn == null)
                qtDir = Environment.GetEnvironmentVariable("QTDIR");
            else
                qtDir = qtDirIn;

            if (qtDir == null)
                return;

            // make QtDir more consistent
            qtDir = new FileInfo(qtDir).FullName.ToLower();     // ### do we really need to convert qtDir to lower case?

            SetupPlatformSpecificData();

            // Find version number
            try {
                var qmakeQuery = new QMakeQuery(this);
                var strVersion = qmakeQuery.query("QT_VERSION");
                if (qmakeQuery.ErrorValue == 0 && strVersion.Length > 0) {
                    var versionParts = strVersion.Split('.');
                    if (versionParts.Length != 3) {
                        qtDir = null;
                        return;
                    }
                    qtMajor = uint.Parse(versionParts[0]);
                    qtMinor = uint.Parse(versionParts[1]);
                    qtPatch = uint.Parse(versionParts[2]);
                } else {
                    var inF = new StreamReader(Locate_qglobal_h());
                    var rgxpVersion = new Regex("#define\\s*QT_VERSION\\s*0x(?<number>\\d+)", RegexOptions.Multiline);
                    var contents = inF.ReadToEnd();
                    inF.Close();
                    var matchObj = rgxpVersion.Match(contents);
                    if (!matchObj.Success) {
                        qtDir = null;
                        return;
                    }

                    strVersion = matchObj.Groups[1].ToString();
                    var version = Convert.ToUInt32(strVersion, 16);
                    qtMajor = version >> 16;
                    qtMinor = (version >> 8) & 0xFF;
                    qtPatch = version & 0xFF;
                }

                if (qtMajor == 5) {
                    qt5Version = true;
                } else {
                    qt5Version = false;
                }
            } catch (Exception /*e*/) {
                qtDir = null;
            }

            try {
                var qmakeQuery = new QMakeQuery(this);
                QtInstallDocs = qmakeQuery.query("QT_INSTALL_DOCS");
            } catch { }
        }

        public string QtInstallDocs
        {
            get; private set;
        }

        public bool IsStaticBuild()
        {
            if (qtConfig == null)
                qtConfig = new QtConfig(qtDir);
            return qtConfig.IsStaticBuild;
        }

        public string GetSignatureFile()
        {
            if (qtConfig == null)
                qtConfig = new QtConfig(qtDir);
            return qtConfig.SignatureFile;
        }

        public string GetQMakeConfEntry(string entryName)
        {
            if (qmakeConf == null)
                qmakeConf = new QMakeConf(this);
            return qmakeConf.Get(entryName);
        }

        /// <summary>
        /// Returns the platform name in a way Visual Studio understands.
        /// </summary>
        public string GetVSPlatformName()
        {
            return vsPlatformName;
        }

        /// <summary>
        /// Read platform name from qmake.conf.
        /// </summary>
        private void SetupPlatformSpecificData()
        {
            if (qmakeConf == null)
                qmakeConf = new QMakeConf(this); // TODO: Do we need this?
            vsPlatformName = (is64Bit()) ? @"x64" : @"Win32";
        }

        private string Locate_qglobal_h()
        {
            string[] candidates = {qtDir + "\\include\\qglobal.h",
                                   qtDir + "\\src\\corelib\\global\\qglobal.h",
                                   qtDir + "\\include\\QtCore\\qglobal.h"};

            foreach (string filename in candidates) {
                if (File.Exists(filename)) {
                    // check whether we look at the real qglobal.h or just a "pointer"
                    var inF = new StreamReader(filename);
                    var rgxpVersion = new Regex("#include\\s+\"(.+global.h)\"", RegexOptions.Multiline);
                    var matchObj = rgxpVersion.Match(inF.ReadToEnd());
                    inF.Close();
                    if (!matchObj.Success)
                        return filename;

                    if (matchObj.Groups.Count >= 2) {
                        var origCurrentDirectory = Directory.GetCurrentDirectory();
                        Directory.SetCurrentDirectory(filename.Substring(0, filename.Length - 10));   // remove "\\qglobal.h"
                        var absIncludeFile = Path.GetFullPath(matchObj.Groups[1].ToString());
                        Directory.SetCurrentDirectory(origCurrentDirectory);
                        if (File.Exists(absIncludeFile))
                            return absIncludeFile;
                    }
                }
            }

            throw new QtVSException("qglobal.h not found");
        }

        internal static class NativeMethods
        {
            [DllImport("kernel32.dll",
                BestFitMapping = false,
                CharSet = CharSet.Auto,
                SetLastError = true)
            ]
            [ResourceExposure(ResourceScope.None)]
            internal static extern int GetBinaryType(string lpApplicationName, ref int lpBinaryType);
        }

        public bool is64Bit()
        {
            // ### This does not work for x64 cross builds of Qt.
            // ### In that case qmake.exe is 32 bit but the DLLs are 64 bit.
            // ### So actually we should check QtCore4.dll / QtCored4.dll instead.
            // ### Unfortunately there's no Win API for checking the architecture of DLLs.
            // ### We must read the PE header instead.
            string fileToCheck = qtDir + "\\bin\\qmake.exe";
            if (!File.Exists(fileToCheck))
                throw new QtVSException("Can't find " + fileToCheck);

            const int SCS_32BIT_BINARY = 0;
            const int SCS_64BIT_BINARY = 6;
            int binaryType = 0;
            bool success = NativeMethods.GetBinaryType(fileToCheck, ref binaryType) != 0;
            if (!success)
                throw new QtVSException("GetBinaryTypeA failed");

            if (binaryType == SCS_32BIT_BINARY)
                return false;
            else if (binaryType == SCS_64BIT_BINARY)
                return true;

            throw new QtVSException("GetBinaryTypeA return unknown executable format for " + fileToCheck);
        }
    }
}

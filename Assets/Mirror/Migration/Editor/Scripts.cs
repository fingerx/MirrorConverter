﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Mirror.MigrationUtilities {
    public class Scripts : MonoBehaviour {

        // private variables that don't need to be modified.
        static readonly string scriptExtension = "*.cs";

        public static readonly string[] knownIncompatibleRegexes = {
                "SyncListStruct",   // this probably needs improvement but i didn't want to duplicate lines of code
                @"\[Command([^\],]*)\]",    // Commands over non-reliable channels
                @"\[ClientRpc([^\],]*)\]",  // ClientRPCs over non-reliable channels
                @"\[TargetRpc([^\],]*)\]",  // TargetRPCs over non-reliable channels
                @"\[SyncEvent([^\],]*)\]",   // SyncEvents (over non-reliable channels) - seriously?
                "NetworkHash128",
                "NetworkInstanceId",
                "GetNetworkSendInterval()",
                "NetworkServer.connections"
            };

        public static readonly string[] knownCompatibleReplacements = {
                "SyncListSTRUCT",   // because mirror's version is moar bettah.
                "[Command]",
                "[ClientRpc]",
                "[TargetRpc]",
                "[SyncEvent]",
                "System.Guid",
                "uint",
                "syncInterval",
                "NetworkServer.connections.Values"
            };

        static int filesModified = 0;
        static string scriptBuffer = string.Empty;
        static MatchCollection matches;

        // Logic portion begins below.

        public static void ScriptsMigration() {
            // Safeguard in case a developer goofs up
            if (knownIncompatibleRegexes.Length != knownCompatibleReplacements.Length) {
                Debug.LogError("[Mirror Migration Tool] BUG DETECTED: Regexes to search for DO NOT match the Regex Replacements. Cannot continue.\nPlease re-download the converter.");
                return;
            }

            // Place holder for the assets folder location.
            string assetsFolder = Application.dataPath;
            // List structure for the CSharp files.
            List<string> filesToScanAndModify = new List<string>();

            // Be verbose and say what's happening.
            Debug.Log("[Mirror Migration Tool] Determined your asset folder is at: " + assetsFolder);
            Debug.Log("[Mirror Migration Tool] Scanning your C# scripts... This might take a moment.");

            // Now we scan the directory...
            try {
                DirectoryInfo dirInfo = new DirectoryInfo(assetsFolder);
                IEnumerable<FileInfo> potentialFiles = dirInfo.GetFiles(scriptExtension, SearchOption.AllDirectories).Where(x => !x.DirectoryName.Contains(@"\Mirror\"));

                // For every entry in this structure add it to the list.
                // SearchOption.AllDirectories will traverse the directory stack
                foreach (FileInfo potentialFile in potentialFiles) {
                    // DEBUG ONLY. This will cause massive Unity Console Spammage!
                    // Debug.Log("[Mirror Migration Tool] DEBUG: Scanned " + potentialFile.FullName);
                    filesToScanAndModify.Add(potentialFile.FullName);
                }

                // Final chance to abort.
                if (!EditorUtility.DisplayDialog("Continue?", string.Format("We've found {0} file(s) that may need updating. Depending on your hardware and storage, " +
                    "this might take a while. Do you wish to continue the process?", filesToScanAndModify.Count), "Go ahead!", "Abort")) {
                    EditorUtility.DisplayDialog("Aborted", "You opted to abort the migration process. Please come back once you're ready to migrate.", "Got it");
                    return;
                }

                bool backupFiles = false;
                if (EditorUtility.DisplayDialog("Scripts Backup", "Do you want to backup each script which are going to be converted?\n" +
                "If so, each script will be saved as .bak file. You can delete it later if needed.",
                "Yes", "No")) {
                    backupFiles = true;
                }

                // Okay, let's do this!
                ProcessFiles(filesToScanAndModify, backupFiles);

                Debug.Log("[Mirror Migration Tool] Processed (and patched, if required) " + filesModified + " files");

                EditorUtility.DisplayDialog("Migration complete.", "Congratulations, you should now be Mirror Network ready.\n\n" +
                    "Thank you for using Mirror and Telepathy Networking Stack for Unity!\n\nPlease don't forget to drop by the GitHub " +
                    "repository to keep up to date and the Discord server if you have any problems. Have fun!", "Awesome");
                return;

            } catch (System.Exception ex) {
                EditorUtility.DisplayDialog("Oh no!", "An exception occurred. If you think this is a Mirror Networking bug, please file a bug report on the GitHub repository." +
                    "It could also be a logic bug in the Migration Tool itself. I encountered the following exception:\n\n" + ex.ToString(), "Okay");
                Cleanup();
                return;
            }
        }

        private static void ProcessFiles(List<string> filesToProcess, bool backupFiles) {
            StreamReader sr;
            StreamWriter sw;

            foreach (string file in filesToProcess) {
                try {
                    // Open and load it into the script buffer.
                    using (sr = new StreamReader(file)) {
                        scriptBuffer = sr.ReadToEnd();
                        sr.Close();
                    }

                    // store initial buffer to use in final comparison before writing out file
                    var initialBuffer = scriptBuffer;

                    if (scriptBuffer.Contains("UnityWebRequest") && !scriptBuffer.Contains("using UnityWebRequest = UnityEngine.Networking.UnityWebRequest;")) {
                        int correctIndex = scriptBuffer.IndexOf("using UnityEngine.Networking;");
                        scriptBuffer = scriptBuffer.Insert(correctIndex, "using UnityWebRequest = UnityEngine.Networking.UnityWebRequest;" + System.Environment.NewLine);
                    }

                    // Get outta here, UnityEngine.Networking !
                    scriptBuffer = scriptBuffer.Replace("using UnityEngine.Networking;", "using Mirror;");

                    // Work our magic.
                    for (int i = 0; i < knownIncompatibleRegexes.Length; i++) {
                        matches = Regex.Matches(scriptBuffer, knownIncompatibleRegexes[i]);
                        if (matches.Count > 0) {
                            // It was successful - replace it.
                            scriptBuffer = Regex.Replace(scriptBuffer, knownIncompatibleRegexes[i], knownCompatibleReplacements[i]);
                        }
                    }

                    // Be extra gentle with some like NetworkSettings directives.
                    matches = Regex.Matches(scriptBuffer, @"NetworkSettings\(([^\)]*)\)");
                    // A file could have more than one NetworkSettings... better to just do the whole lot.
                    // We don't know what the developer might be doing.
                    if (matches.Count > 0) {
                        for (int i = 0; i < matches.Count; i++) {
                            Match nsm = Regex.Match(matches[i].ToString(), @"(?<=\().+?(?=\))");
                            if (nsm.Success) {
                                string[] netSettingArguments = nsm.ToString().Split(',');
                                if (netSettingArguments.Length > 1) {
                                    string patchedNetSettings = string.Empty;

                                    int a = 0;
                                    foreach (string argument in netSettingArguments) {
                                        // Increment a, because that's how many elements we've looked at.
                                        a++;

                                        // If it contains the offender, just continue, don't do anything.
                                        if (argument.Contains("channel")) continue;

                                        // If it doesn't then add it to our new string.
                                        patchedNetSettings += argument.Trim();
                                        if (a < netSettingArguments.Length) patchedNetSettings += ", ";
                                    }

                                    // a = netSettingArguments.Length; patch it up and there we go.
                                    scriptBuffer = Regex.Replace(scriptBuffer, nsm.Value, patchedNetSettings);
                                } else {
                                    // Replace it.
                                    if (netSettingArguments[0].Contains("channel")) {
                                        // Don't touch this.
                                        scriptBuffer = scriptBuffer.Replace(string.Format("[{0}]", matches[i].Value), string.Empty);
                                    }
                                    // DONE!
                                }
                            }
                        }
                    }

                    // Backup the old files for safety.
                    // The user can delete them later.
                    if (backupFiles && !File.Exists(file + ".bak"))
                        File.Copy(file, file + ".bak");

                    // Now the job is done, we want to write the data out to disk ONLY if the contents were actually changed... 
                    if (initialBuffer != scriptBuffer) {
                        using (sw = new StreamWriter(file, false, new UTF8Encoding(false))) {
                            sw.Write(scriptBuffer.TrimStart());
                            sw.Close();
                        }
                    }

                    // Increment the modified counter for statistics.
                    filesModified++;
                } catch (System.Exception e) {
                    // Kaboom, this tool ate something it shouldn't have.
                    Debug.LogError(string.Format("[Mirror Migration Tool] Encountered an exception processing {0}:\n{1}", file, e.ToString()));
                }
            }
        }

        /// <summary>
        /// Cleans up after the migration tool is completed or has failed.
        /// </summary>
        public static void Cleanup() {
            scriptBuffer = string.Empty;
            matches = null;
            filesModified = 0;
        }
    }
}

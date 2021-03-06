// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using EnvDTE90;
using EnvDTE90a;
using Microsoft.PythonTools;
using Microsoft.PythonTools.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudioTools;
using Microsoft.VisualStudioTools.VSTestHost;
using TestUtilities;
using TestUtilities.Python;
using TestUtilities.UI;
using TestUtilities.UI.Python;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Thread = System.Threading.Thread;

namespace DebuggerUITests {
    [TestClass]
    public class DebugProject {
        [ClassInitialize]
        public static void DoDeployment(TestContext context) {
            AssertListener.Initialize();
            PythonTestData.Deploy();
        }

        bool PrevWaitOnNormalExit;

        [TestInitialize]
        public void MyTestInit() {
            var options = GetOptions();
            PrevWaitOnNormalExit = options.WaitOnNormalExit;
            options.WaitOnNormalExit = false;
        }

        [TestCleanup]
        public void MyTestCleanup() {
            GetOptions().WaitOnNormalExit = PrevWaitOnNormalExit;
        }

        #region Test Cases

        /// <summary>
        /// Loads the simple project and then unloads it, ensuring that the solution is created with a single project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void DebugPythonProject() {
            using (var app = new VisualStudioApp()) {
                StartHelloWorldAndBreak(app);

                app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
            }
        }

        /// <summary>
        /// Loads a project with the startup file in a subdirectory, ensuring that syspath is correct when debugging.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void DebugPythonProjectSubFolderStartupFileSysPath() {
            using (var app = new VisualStudioApp()) {
                app.OpenProject(TestData.GetPath(@"TestData\SysPath.sln"));

                ClearOutputWindowDebugPaneText();
                app.Dte.ExecuteCommand("Debug.Start");
                WaitForMode(app, dbgDebugMode.dbgDesignMode);

                // sys.path should point to the startup file directory, not the project directory.
                // this matches the behavior of start without debugging.
                // Note: backslashes are escaped in the output
                string testDataPath = TestData.GetPath("TestData\\SysPath\\Sub'").Replace("\\", "\\\\");
                WaitForDebugOutput(text => text.Contains(testDataPath));
            }
        }

        /// <summary>
        /// Debugs a project with and without a process-wide PYTHONPATH value.
        /// If <see cref="DebugPythonProjectSubFolderStartupFileSysPath"/> fails
        /// this test may also fail.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void DebugPythonProjectWithAndWithoutClearingPythonPath() {
            var origPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
            string testDataPath = TestData.GetPath("TestData\\HelloWorld").Replace("\\", "\\\\");
            Environment.SetEnvironmentVariable("PYTHONPATH", testDataPath);
            try {
                using (var app = new VisualStudioApp()) {
                    app.OpenProject(TestData.GetPath(@"TestData\SysPath.sln"));

                    var uiThread = app.ServiceProvider.GetUIThread();
                    uiThread.Invoke(() => {
                        app.ServiceProvider.GetPythonToolsService().GeneralOptions.ClearGlobalPythonPath = false;
                    });

                    try {
                        ClearOutputWindowDebugPaneText();
                        app.Dte.ExecuteCommand("Debug.Start");
                        WaitForMode(app, dbgDebugMode.dbgDesignMode);

                        WaitForDebugOutput(text => text.Contains(testDataPath));
                    } finally {
                        uiThread.Invoke(() => {
                            app.ServiceProvider.GetPythonToolsService().GeneralOptions.ClearGlobalPythonPath = true;
                        });
                    }

                    ClearOutputWindowDebugPaneText();
                    app.Dte.ExecuteCommand("Debug.Start");
                    WaitForMode(app, dbgDebugMode.dbgDesignMode);

                    WaitForDebugOutput(text => text.Contains("DONE"));
                    var outputWindowText = GetOutputWindowDebugPaneText();
                    Assert.IsFalse(outputWindowText.Contains(testDataPath), outputWindowText);
                }
            } finally {
                Environment.SetEnvironmentVariable("PYTHONPATH", origPythonPath);
            }
        }

        /// <summary>
        /// Tests using a custom interpreter path that is relative
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void DebugPythonCustomInterpreter() {
            // try once when the interpreter doesn't exist...
            using (var app = new VisualStudioApp()) {
                var project = app.OpenProject(TestData.GetPath(@"TestData\RelativeInterpreterPath.sln"), "Program.py");

                app.Dte.ExecuteCommand("Debug.Start");

                string expectedMissingInterpreterText = string.Format(
                    "The project cannot be launched because no Python interpreter is available at \"{0}\". Please check the " +
                    "Python Environments window and ensure the version of Python is installed and has all settings specified.",
                    TestData.GetPath(@"TestData\Interpreter.exe"));
                var dialog = app.WaitForDialog();
                VisualStudioApp.CheckMessageBox(MessageBoxButton.Ok, expectedMissingInterpreterText);

                app.Dte.Solution.Close(false);

                // copy an interpreter over and try again
                File.Copy(PythonPaths.Python27.InterpreterPath, TestData.GetPath(@"TestData\Interpreter.exe"));
                try {
                    OpenProjectAndBreak(app, TestData.GetPath(@"TestData\RelativeInterpreterPath.sln"), "Program.py", 1);
                    app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                    Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
                } finally {
                    File.Delete(TestData.GetPath(@"TestData\Interpreter.exe"));
                }
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestPendingBreakPointLocation() {
            using (var app = new VisualStudioApp()) {
                var project = app.OpenProject(@"TestData\DebuggerProject.sln", "BreakpointInfo.py");
                var bpInfo = project.ProjectItems.Item("BreakpointInfo.py");

                project.GetPythonProject().GetAnalyzer().WaitForCompleteAnalysis(x => true);

                var bp = app.Dte.Debugger.Breakpoints.Add(File: "BreakpointInfo.py", Line: 2);
                Assert.AreEqual("Python", bp.Item(1).Language);
                // FunctionName doesn't get queried for when adding the BP via EnvDTE, so we can't assert here :(
                //Assert.AreEqual("BreakpointInfo.C", bp.Item(1).FunctionName);
                bp = app.Dte.Debugger.Breakpoints.Add(File: "BreakpointInfo.py", Line: 3);
                Assert.AreEqual("Python", bp.Item(1).Language);
                //Assert.AreEqual("BreakpointInfo.C.f", bp.Item(1).FunctionName);
                bp = app.Dte.Debugger.Breakpoints.Add(File: "BreakpointInfo.py", Line: 6);
                Assert.AreEqual("Python", bp.Item(1).Language);
                //Assert.AreEqual("BreakpointInfo", bp.Item(1).FunctionName);
                bp = app.Dte.Debugger.Breakpoints.Add(File: "BreakpointInfo.py", Line: 7);
                Assert.AreEqual("Python", bp.Item(1).Language);
                //Assert.AreEqual("BreakpointInfo.f", bp.Item(1).FunctionName);

                // https://github.com/Microsoft/PTVS/pull/630
                // Make sure 
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestBoundBreakpoint() {
            using (var app = new VisualStudioApp()) {
                var project = OpenDebuggerProjectAndBreak(app, "BreakpointInfo.py", 2);

                var pendingBp = (Breakpoint3)app.Dte.Debugger.Breakpoints.Item(1);
                Assert.AreEqual(1, pendingBp.Children.Count);

                var bp = (Breakpoint3)pendingBp.Children.Item(1);
                Assert.AreEqual("Python", bp.Language);
                Assert.AreEqual(TestData.GetPath(@"TestData\DebuggerProject\BreakpointInfo.py"), bp.File);
                Assert.AreEqual(2, bp.FileLine);
                Assert.AreEqual(1, bp.FileColumn);
                Assert.AreEqual(true, bp.Enabled);
                Assert.AreEqual(true, bp.BreakWhenHit);
                Assert.AreEqual(1, bp.CurrentHits);
                Assert.AreEqual(1, bp.HitCountTarget);
                Assert.AreEqual(dbgHitCountType.dbgHitCountTypeNone, bp.HitCountType);

                // https://github.com/Microsoft/PTVS/pull/630
                pendingBp.BreakWhenHit = false; // causes rebind
                Assert.AreEqual(1, pendingBp.Children.Count);
                bp = (Breakpoint3)pendingBp.Children.Item(1);
                Assert.AreEqual(false, bp.BreakWhenHit);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestStep() {
            using (var app = new VisualStudioApp()) {
                var project = OpenDebuggerProjectAndBreak(app, "SteppingTest.py", 1);
                app.Dte.Debugger.StepOver(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);

                Assert.AreEqual((uint)2, ((StackFrame2)app.Dte.Debugger.CurrentStackFrame).LineNumber);

                app.Dte.Debugger.TerminateAll();

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestShowCallStackOnCodeMap() {
            using (var app = new VisualStudioApp()) {
                var project = OpenDebuggerProjectAndBreak(app, "SteppingTest3.py", 2);
 
                app.Dte.ExecuteCommand("Debug.ShowCallStackonCodeMap");

                // Got the CodeMap Graph displaying, but it may not have finished processing
                app.WaitForInputIdle();

                var dgmlKind = "{295A0962-5A59-4F4F-9E12-6BC670C15C3B}";

                Document dgmlDoc = null;
                for (int i = 1; i <= app.Dte.Documents.Count; i++) {
                    var doc = app.Dte.Documents.Item(i);
                    if (doc.Kind == dgmlKind) {
                        dgmlDoc = doc;
                        break;
                    }
                }

                Assert.IsNotNull(dgmlDoc, "Could not find dgml document");

                var dgmlFile = Path.GetTempFileName();
                try {
                    // Save to a temp file. If the code map is not ready, it 
                    // may have template xml but no data in it, so give it
                    // some more time and try again.
                    string fileText = string.Empty;
                    for (int i = 0; i < 10; i++) {
                        dgmlDoc.Save(dgmlFile);

                        fileText = File.ReadAllText(dgmlFile);
                        if (fileText.Contains("SteppingTest3")) {
                            break;
                        }

                        Thread.Sleep(250);
                    }

                    // These are the lines of interest in the DGML File.  If these match, the correct content should be displayed in the code map.
                    List<string> LinesToMatch = new List<string>() {
                        @"<Node Id=""\(Name=f @1 IsUnresolved=True\)"" Category=""CodeSchema_CallStackUnresolvedMethod"" Label=""f"">",
                        @"<Node Id=""@2"" Category=""CodeSchema_CallStackUnresolvedMethod"" Label=""SteppingTest3 module"">",
                        @"<Node Id=""ExternalCodeRootNode"" Category=""ExternalCallStackEntry"" Label=""External Code"">",
                        @"<Link Source=""@2"" Target=""\(Name=f @1 IsUnresolved=True\)"" Category=""CallStackDirectCall"">",
                        @"<Alias n=""1"" Uri=""Assembly=SteppingTest3"" />",
                        @"<Alias n=""2"" Id=""\(Name=&quot;SteppingTest3 module&quot; @1 IsUnresolved=True\)"" />"
                    };

                    foreach (var line in LinesToMatch) {
                        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(fileText, line), "Expected:\r\n{0}\r\nsActual:\r\n{1}", line, fileText);
                    }
                } finally {
                    File.Delete(dgmlFile);
                }
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestStep3() {
            using (var app = new VisualStudioApp()) {
                var project = OpenDebuggerProjectAndBreak(app, "SteppingTest3.py", 2);
                app.Dte.Debugger.StepOut(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);

                Assert.AreEqual((uint)5, ((StackFrame2)app.Dte.Debugger.CurrentStackFrame).LineNumber);

                app.Dte.Debugger.TerminateAll();

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestStep5() {
            using (var app = new VisualStudioApp()) {
                var project = OpenDebuggerProjectAndBreak(app, "SteppingTest5.py", 5);
                app.Dte.Debugger.StepInto(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);

                Assert.AreEqual((uint)2, ((StackFrame2)app.Dte.Debugger.CurrentStackFrame).LineNumber);

                app.Dte.Debugger.TerminateAll();

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestSetNextLine() {
            using (var app = new VisualStudioApp()) {
                var project = OpenDebuggerProjectAndBreak(app, "SetNextLine.py", 7);

                var doc = app.Dte.Documents.Item("SetNextLine.py");
                ((TextSelection)doc.Selection).GotoLine(8);
                ((TextSelection)doc.Selection).EndOfLine(false);
                //((TextSelection)doc.Selection).CharRight(false, 5);
                //((TextSelection)doc.Selection).CharRight(true, 1);
                var curLine = ((TextSelection)doc.Selection).CurrentLine;

                app.Dte.Debugger.SetNextStatement();
                app.Dte.Debugger.StepOver(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);

                var curFrame = app.Dte.Debugger.CurrentStackFrame;
                var local = curFrame.Locals.Item("y");
                Assert.AreEqual("100", local.Value);

                try {
                    curFrame.Locals.Item("x");
                    Assert.Fail("Expected exception, x should not be defined");
                } catch {
                }

                app.Dte.Debugger.TerminateAll();

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        /*
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestBreakAll() {
            var project = OpenDebuggerProjectAndBreak("BreakAllTest.py", 1);
            
            app.Dte.Debugger.Go(false);

            
            WaitForMode(app, dbgDebugMode.dbgRunMode);

            Thread.Sleep(2000);

            app.Dte.Debugger.Break();

            WaitForMode(app, dbgDebugMode.dbgBreakMode);

            var lineNo = ((StackFrame2)app.Dte.Debugger.CurrentStackFrame).LineNumber;
            Assert.IsTrue(lineNo == 1 || lineNo == 2);

            app.Dte.Debugger.Go(false);

            WaitForMode(app, dbgDebugMode.dbgRunMode);

            app.Dte.Debugger.TerminateAll();

            WaitForMode(app, dbgDebugMode.dbgDesignMode);
        }*/

        /// <summary>
        /// Loads the simple project and then terminates the process while we're at a breakpoint.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestTerminateProcess() {
            using (var app = new VisualStudioApp()) {
                StartHelloWorldAndBreak(app);

                Assert.AreEqual(dbgDebugMode.dbgBreakMode, app.Dte.Debugger.CurrentMode);
                Assert.AreEqual(1, app.Dte.Debugger.BreakpointLastHit.FileLine);

                app.Dte.Debugger.TerminateAll();

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        /// <summary>
        /// Loads the simple project and makes sure we get the correct module.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestEnumModules() {
            using (var app = new VisualStudioApp()) {
                StartHelloWorldAndBreak(app);

                var modules = ((Process3)app.Dte.Debugger.CurrentProcess).Modules;
                Assert.IsTrue(modules.Count >= 1);

                var module = modules.Item("Program");
                Assert.IsNotNull(module);

                Assert.IsTrue(module.Path.EndsWith("Program.py"));
                Assert.AreEqual("Program", module.Name);
                Assert.AreNotEqual((uint)0, module.Order);

                app.Dte.Debugger.TerminateAll();
                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestThread() {
            using (var app = new VisualStudioApp()) {
                StartHelloWorldAndBreak(app);

                var thread = ((Thread2)app.Dte.Debugger.CurrentThread);
                Assert.AreEqual("MainThread", thread.Name);
                Assert.AreEqual(0, thread.SuspendCount);
                Assert.AreEqual("Normal", thread.Priority);
                Assert.AreEqual("MainThread", thread.DisplayName);
                thread.DisplayName = "Hi";
                Assert.AreEqual("Hi", thread.DisplayName);

                app.Dte.Debugger.TerminateAll();
                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void ExpressionEvaluation() {
            using (var app = new VisualStudioApp()) {
                OpenDebuggerProject(app, "Program.py");

                app.Dte.Debugger.Breakpoints.Add(File: "Program.py", Line: 14);
                app.Dte.ExecuteCommand("Debug.Start");

                WaitForMode(app, dbgDebugMode.dbgBreakMode);

                Assert.AreEqual(14, app.Dte.Debugger.BreakpointLastHit.FileLine);

                Assert.AreEqual("i", app.Dte.Debugger.GetExpression("i").Name);
                Assert.AreEqual("42", app.Dte.Debugger.GetExpression("i").Value);
                Assert.AreEqual("int", app.Dte.Debugger.GetExpression("i").Type);
                Assert.IsTrue(app.Dte.Debugger.GetExpression("i").IsValidValue);
                Assert.AreEqual(0, app.Dte.Debugger.GetExpression("i").DataMembers.Count);

                var curFrame = app.Dte.Debugger.CurrentStackFrame;

                var local = curFrame.Locals.Item("i");
                Assert.AreEqual("42", local.Value);
                Assert.AreEqual("f", curFrame.FunctionName);
                Assert.IsTrue(((StackFrame2)curFrame).FileName.EndsWith("Program.py"));
                Assert.AreEqual((uint)14, ((StackFrame2)curFrame).LineNumber);
                Assert.AreEqual("Program", ((StackFrame2)curFrame).Module);

                Assert.AreEqual(3, curFrame.Locals.Item("l").DataMembers.Count);
                Assert.AreEqual("[0]", curFrame.Locals.Item("l").DataMembers.Item(1).Name);

                Assert.AreEqual(3, ((StackFrame2)curFrame).Arguments.Count);
                Assert.AreEqual("a", ((StackFrame2)curFrame).Arguments.Item(1).Name);
                Assert.AreEqual("2", ((StackFrame2)curFrame).Arguments.Item(1).Value);

                var expr = ((Debugger3)app.Dte.Debugger).GetExpression("l[0] + l[1]");
                Assert.AreEqual("l[0] + l[1]", expr.Name);
                Assert.AreEqual("5", expr.Value);

                expr = ((Debugger3)app.Dte.Debugger).GetExpression("invalid expression");
                Assert.IsFalse(expr.IsValidValue);

                app.Dte.Debugger.ExecuteStatement("x = 2");

                app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestException() {
            ExceptionTest("SimpleException.py", "Exception Thrown", "Exception", "Exception", 3);
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestException2() {
            ExceptionTest("SimpleException2.py", "Exception Thrown", "ValueError: bad value", "ValueError", 3);
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestExceptionUnhandled() {
            var waitOnAbnormalExit = GetOptions().WaitOnAbnormalExit;
            GetOptions().WaitOnAbnormalExit = false;
            try {
                ExceptionTest("SimpleExceptionUnhandled.py", "Exception User-Unhandled", "ValueError: bad value", "ValueError", 2);
            } finally {
                GetOptions().WaitOnAbnormalExit = waitOnAbnormalExit;
            }
        }

        // https://github.com/Microsoft/PTVS/issues/275
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestExceptionInImportLibNotReported() {
            using (var app = new VisualStudioApp()) {
                bool justMyCode = (bool)app.Dte.Properties["Debugging", "General"].Item("EnableJustMyCode").Value;
                app.Dte.Properties["Debugging", "General"].Item("EnableJustMyCode").Value = true;
                try {
                    OpenDebuggerProjectAndBreak(app, "ImportLibException.py", 2);
                    app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                    Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
                } finally {
                    app.Dte.Properties["Debugging", "General"].Item("EnableJustMyCode").Value = justMyCode;
                }
            }
        }

        private static void ExceptionTest(string filename, string expectedTitle, string expectedDescription, string exceptionType, int expectedLine) {
            using (var app = new VisualStudioApp()) {
                var debug3 = (Debugger3)app.Dte.Debugger;
                bool justMyCode = (bool)app.Dte.Properties["Debugging", "General"].Item("EnableJustMyCode").Value;
                app.Dte.Properties["Debugging", "General"].Item("EnableJustMyCode").Value = true;
                try {

                    OpenDebuggerProject(app, filename);

                    var exceptionSettings = debug3.ExceptionGroups.Item("Python Exceptions");

                    exceptionSettings.SetBreakWhenThrown(true, exceptionSettings.Item(exceptionType));

                    app.Dte.ExecuteCommand("Debug.Start");
                    WaitForMode(app, dbgDebugMode.dbgBreakMode);

                    exceptionSettings.SetBreakWhenThrown(false, exceptionSettings.Item(exceptionType));
                    exceptionSettings.SetBreakWhenThrown(true, exceptionSettings.Item(exceptionType));
                    debug3.ExceptionGroups.ResetAll();

                    var excepAdorner = app.WaitForExceptionAdornment();
                    AutomationWrapper.DumpElement(excepAdorner.Element);

                    Assert.AreEqual(expectedDescription, excepAdorner.Description.TrimEnd());
                    Assert.AreEqual(expectedTitle, excepAdorner.Title.TrimEnd());

                    Assert.AreEqual((uint)expectedLine, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);

                    debug3.Go(WaitForBreakOrEnd: true);

                    WaitForMode(app, dbgDebugMode.dbgDesignMode);
                } finally {
                    app.Dte.Properties["Debugging", "General"].Item("EnableJustMyCode").Value = true;
                }
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestBreakpoints() {
            using (var app = new VisualStudioApp()) {
                OpenDebuggerProjectAndBreak(app, "BreakpointTest2.py", 3);
                var debug3 = (Debugger3)app.Dte.Debugger;
                Assert.AreEqual((uint)3, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                debug3.Go(true);
                Assert.AreEqual((uint)3, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                Assert.IsTrue(debug3.Breakpoints.Item(1).Enabled);
                debug3.Breakpoints.Item(1).Delete();
                debug3.Go(true);

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestBreakpointsDisable() {
            using (var app = new VisualStudioApp()) {
                OpenDebuggerProjectAndBreak(app, "BreakpointTest4.py", 2);
                var debug3 = (Debugger3)app.Dte.Debugger;
                Assert.AreEqual((uint)2, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                debug3.Go(true);
                Assert.AreEqual((uint)2, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                Assert.IsTrue(debug3.Breakpoints.Item(1).Enabled);
                debug3.Breakpoints.Item(1).Enabled = false;
                debug3.Go(true);

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestBreakpointsDisableReenable() {
            using (var app = new VisualStudioApp()) {
                var debug3 = (Debugger3)app.Dte.Debugger;
                OpenDebuggerProjectAndBreak(app, "BreakpointTest4.py", 2);
                Assert.AreEqual((uint)2, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                debug3.Go(true);
                Assert.AreEqual((uint)2, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                int bpCount = debug3.Breakpoints.Count;

                Assert.AreEqual(1, bpCount);
                Assert.IsTrue(debug3.Breakpoints.Item(1).Enabled);
                Assert.AreEqual(2, debug3.Breakpoints.Item(1).FileLine);
                debug3.Breakpoints.Item(1).Enabled = false;

                debug3.Breakpoints.Add(File: "BreakpointTest4.py", Line: 4);
                debug3.Breakpoints.Add(File: "BreakpointTest4.py", Line: 5);
                Assert.AreEqual(4, debug3.Breakpoints.Item(2).FileLine);
                Assert.AreEqual(5, debug3.Breakpoints.Item(3).FileLine);

                // line 4
                debug3.Go(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual((uint)4, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);

                // line 5
                debug3.Go(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual((uint)5, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                debug3.Breakpoints.Item(3).Enabled = false;

                // back to line 4
                debug3.Go(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual((uint)4, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);

                debug3.Go(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual((uint)4, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);

                debug3.Breakpoints.Item(2).Enabled = false;
                debug3.Breakpoints.Item(3).Enabled = true;

                // back to line 5
                debug3.Go(true);
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual((uint)5, ((StackFrame2)debug3.CurrentThread.StackFrames.Item(1)).LineNumber);
                debug3.Breakpoints.Item(3).Enabled = false;

                // all disabled, run to completion
                debug3.Go(true);
                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            }
        }

        /// <summary>
        /// Make sure the presence of errors causes F5 to prevent running w/o a confirmation.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void TestLaunchWithErrorsDontRun() {
            var app = new PythonVisualStudioApp();
            var originalValue = GetOptions().PromptBeforeRunningWithBuildErrorSetting;
            GetOptions().PromptBeforeRunningWithBuildErrorSetting = true;
            try {
                var project = app.OpenProject(@"TestData\ErrorProject.sln");

                // Open a file with errors
                string scriptFilePath = TestData.GetPath(@"TestData\ErrorProject\Program.py");
                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.ExecuteCommand("View.ErrorList");
                var items = app.WaitForErrorListItems(7);

                var debug3 = (Debugger3)app.Dte.Debugger;
                ThreadPool.QueueUserWorkItem(x => debug3.Go(true));

                var dialog = new PythonLaunchWithErrorsDialog(app.WaitForDialog());
                dialog.No();

                // make sure we don't go into debug mode
                for (int i = 0; i < 10; i++) {
                    Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
                    System.Threading.Thread.Sleep(100);
                }

                WaitForMode(app, dbgDebugMode.dbgDesignMode);
            } finally {
                GetOptions().PromptBeforeRunningWithBuildErrorSetting = originalValue;
                app.Dispose();
            }
        }

        /// <summary>
        /// Start with debugging, with script but no project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithDebuggingNoProject() {
            string scriptFilePath = TestData.GetPath(@"TestData\HelloWorld\Program.py");

            using (var app = new VisualStudioApp()) {
                app.DeleteAllBreakPoints();

                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.Debugger.Breakpoints.Add(File: scriptFilePath, Line: 1);
                app.Dte.ExecuteCommand("Python.StartWithDebugging");
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual(dbgDebugMode.dbgBreakMode, app.Dte.Debugger.CurrentMode);
                Assert.IsNotNull(app.Dte.Debugger.BreakpointLastHit);
                Assert.AreEqual("Program.py, line 1", app.Dte.Debugger.BreakpointLastHit.Name);
                app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
            }
        }

        /// <summary>
        /// Start without debugging, with script but no project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithoutDebuggingNoProject() {
            string scriptFilePath = TestData.GetPath(@"TestData\CreateFile1.py");

            using (var app = new VisualStudioApp()) {
                app.DeleteAllBreakPoints();

                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.Debugger.Breakpoints.Add(File: scriptFilePath, Line: 1);
                app.Dte.ExecuteCommand("Python.StartWithoutDebugging");
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
                WaitForFileCreatedByScript(TestData.GetPath(@"TestData\File1.txt"));
            }
        }

        /// <summary>
        /// Start with debugging, with script not in project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithDebuggingNotInProject() {
            string scriptFilePath = TestData.GetPath(@"TestData\HelloWorld\Program.py");

            using (var app = new VisualStudioApp()) {
                OpenDebuggerProject(app);

                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.Debugger.Breakpoints.Add(File: scriptFilePath, Line: 1);
                app.Dte.ExecuteCommand("Python.StartWithDebugging");
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual(dbgDebugMode.dbgBreakMode, app.Dte.Debugger.CurrentMode);
                Assert.AreEqual("Program.py, line 1", app.Dte.Debugger.BreakpointLastHit.Name);
                app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
            }
        }

        /// <summary>
        /// Start without debugging, with script not in project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithoutDebuggingNotInProject() {
            string scriptFilePath = TestData.GetPath(@"TestData\CreateFile2.py");

            using (var app = new VisualStudioApp()) {
                OpenDebuggerProject(app);

                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.Debugger.Breakpoints.Add(File: scriptFilePath, Line: 1);
                app.Dte.ExecuteCommand("Python.StartWithoutDebugging");
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
                WaitForFileCreatedByScript(TestData.GetPath(@"TestData\File2.txt"));
            }
        }

        /// <summary>
        /// Start with debuggging, with script in project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithDebuggingInProject() {
            string scriptFilePath = TestData.GetPath(@"TestData\DebuggerProject\Program.py");

            using (var app = new VisualStudioApp()) {
                OpenDebuggerProject(app);

                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.Debugger.Breakpoints.Add(File: scriptFilePath, Line: 1);
                app.Dte.ExecuteCommand("Python.StartWithDebugging");
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual(dbgDebugMode.dbgBreakMode, app.Dte.Debugger.CurrentMode);
                Assert.AreEqual("Program.py, line 1", app.Dte.Debugger.BreakpointLastHit.Name);
                app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
            }
        }

        /// <summary>
        /// Start with debuggging, with script in subfolder project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithDebuggingSubfolderInProject() {
            string scriptFilePath = TestData.GetPath(@"TestData\DebuggerProject\Sub\paths.py");

            using (var app = new VisualStudioApp()) {
                OpenDebuggerProject(app);

                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.Debugger.Breakpoints.Add(File: scriptFilePath, Line: 3);
                app.Dte.ExecuteCommand("Python.StartWithDebugging");
                WaitForMode(app, dbgDebugMode.dbgBreakMode);
                Assert.AreEqual(dbgDebugMode.dbgBreakMode, app.Dte.Debugger.CurrentMode);
                AssertUtil.ContainsAtLeast(
                    app.Dte.Debugger.GetExpression("sys.path").DataMembers.Cast<Expression>().Select(e => e.Value),
                    "'" + TestData.GetPath(@"TestData\DebuggerProject").Replace("\\", "\\\\") + "'"
                );
                Assert.AreEqual(
                    "'" + TestData.GetPath(@"TestData\DebuggerProject").Replace("\\", "\\\\") + "'",
                    app.Dte.Debugger.GetExpression("os.path.abspath(os.curdir)").Value
                );
                app.Dte.Debugger.Go(WaitForBreakOrEnd: true);
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
            }
        }

        /// <summary>
        /// Start without debuggging, with script in project.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithoutDebuggingInProject() {
            string scriptFilePath = TestData.GetPath(@"TestData\DebuggerProject\CreateFile3.py");

            using (var app = new VisualStudioApp()) {
                OpenDebuggerProject(app);

                app.Dte.ItemOperations.OpenFile(scriptFilePath);
                app.Dte.Debugger.Breakpoints.Add(File: scriptFilePath, Line: 1);
                app.Dte.ExecuteCommand("Python.StartWithoutDebugging");
                Assert.AreEqual(dbgDebugMode.dbgDesignMode, app.Dte.Debugger.CurrentMode);
                WaitForFileCreatedByScript(TestData.GetPath(@"TestData\DebuggerProject\File3.txt"));
            }
        }

        /// <summary>
        /// Start with debugging, no script.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithDebuggingNoScript() {
            try {
                VSTestContext.DTE.ExecuteCommand("Python.StartWithDebugging");
            } catch (COMException e) {
                // Requires an opened python file with focus
                Assert.IsTrue(e.ToString().Contains("is not available"));
            }
        }

        /// <summary>
        /// Start without debugging, no script.
        /// </summary>
        [TestMethod, Priority(1)]
        [HostType("VSTestHost"), TestCategory("Installed")]
        public void StartWithoutDebuggingNoScript() {
            try {
                VSTestContext.DTE.ExecuteCommand("Python.StartWithoutDebugging");
            } catch (COMException e) {
                // Requires an opened python file with focus
                Assert.IsTrue(e.ToString().Contains("is not available"));
            }
        }

        private static void WaitForFileCreatedByScript(string createdFilePath) {
            bool exists = false;
            for (int i = 0; i < 10; i++) {
                exists = File.Exists(createdFilePath);
                if (exists) {
                    break;
                }
                System.Threading.Thread.Sleep(250);
            }

            Assert.IsTrue(exists, "Python script was expected to create file '{0}'.", createdFilePath);
        }

        protected static IPythonOptions GetOptions() {
            return (IPythonOptions)VSTestContext.DTE.GetObject("VsPython");
        }

        #endregion

        #region Helpers


        internal static Project OpenDebuggerProject(VisualStudioApp app, string startItem = null) {
            return app.OpenProject(@"TestData\DebuggerProject.sln", startItem);
        }

        private static Project OpenDebuggerProjectAndBreak(VisualStudioApp app, string startItem, int lineNo, bool setStartupItem = true) {
            return OpenProjectAndBreak(app, @"TestData\DebuggerProject.sln", startItem, lineNo);
        }

        private static void ClearOutputWindowDebugPaneText() {
            OutputWindow window = ((EnvDTE80.DTE2)VSTestContext.DTE).ToolWindows.OutputWindow;
            OutputWindowPane debugPane = window.OutputWindowPanes.Item("Debug");
            debugPane.Clear();
        }

        private static string GetOutputWindowDebugPaneText() {
            OutputWindow window = ((EnvDTE80.DTE2)VSTestContext.DTE).ToolWindows.OutputWindow;
            OutputWindowPane debugPane = window.OutputWindowPanes.Item("Debug");
            debugPane.Activate();
            var debugDoc = debugPane.TextDocument;
            string debugText = debugDoc.StartPoint.CreateEditPoint().GetText(debugDoc.EndPoint);
            return debugText;
        }

        private static void WaitForDebugOutput(Predicate<string> condition) {
            for (int i = 0; i < 50 && !condition(GetOutputWindowDebugPaneText()); i++) {
                Thread.Sleep(100);
            }

            Assert.IsTrue(condition(GetOutputWindowDebugPaneText()));
        }

        private static void StartHelloWorldAndBreak(VisualStudioApp app) {
            OpenProjectAndBreak(app, @"TestData\HelloWorld.sln", "Program.py", 1);
        }

        internal static Project OpenProjectAndBreak(VisualStudioApp app, string projName, string filename, int lineNo, bool setStartupItem = true) {
            var project = app.OpenProject(projName, filename, setStartupItem: setStartupItem);

            app.Dte.Debugger.Breakpoints.Add(File: filename, Line: lineNo);
            app.Dte.ExecuteCommand("Debug.Start");

            WaitForMode(app, dbgDebugMode.dbgBreakMode);

            Assert.IsNotNull(app.Dte.Debugger.BreakpointLastHit);
            Assert.AreEqual(lineNo, app.Dte.Debugger.BreakpointLastHit.FileLine);
            return project;
        }


        internal static void WaitForMode(VisualStudioApp app, dbgDebugMode mode) {
            for (int i = 0; i < 30 && app.Dte.Debugger.CurrentMode != mode; i++) {
                Thread.Sleep(1000);
            }

            Assert.AreEqual(mode, app.Dte.Debugger.CurrentMode);
        }

        #endregion
    }
}
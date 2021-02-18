using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using FMOD.Studio;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Mono.CSharp;
using Monocle;
using MonoMod.Cil;

namespace Celeste.Mod.DebugConsole {
    public class DebugConsole : EverestModule {
        #region everest setup
        public static DebugConsole Instance;
        public bool CaptureInput = false;
        public string Prompt = "C#>";
        public List<string> History = new List<string>();
        public int HistoryIndex = 0;
        public string SavedLine = "";

        [Command("cs", "Start C# interactive session")]
        public static void StartCapture() {
            Instance.CaptureInput = true;
            Instance.SetText(Instance.SavedLine);
            Engine.Commands.Log("Welcome to the C# interactive prompt. Ctrl-C to clear line and Ctrl-D to exit.", Color.GreenYellow);
        }

        public DebugConsole() {
            Instance = this;
        }
        
        public override void LoadContent(bool firstLoad) {
            this.Setup();
        }

        public override void Load() {
            On.Monocle.Commands.HandleKey += this.HandleDebugKeystroke;
            IL.Monocle.Commands.Render += this.CustomPrompt;
        }

        public override void Unload() {
            On.Monocle.Commands.HandleKey -= this.HandleDebugKeystroke;
            IL.Monocle.Commands.Render -= this.CustomPrompt;
        }

        public override void CreateModMenuSection(TextMenu menu, bool inGame, EventInstance snapshot) {
        }

        public string PopText() {
            var field = typeof(Monocle.Commands).GetField("currentText", BindingFlags.NonPublic | BindingFlags.Instance);
            string text = (string) field.GetValue(Engine.Commands);
            field.SetValue(Engine.Commands, "");
            typeof(Monocle.Commands).GetField("charIndex", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(Engine.Commands, 0);
            return text;
        }

        public void SetText(string value) {
            var field = typeof(Monocle.Commands).GetField("currentText", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(Engine.Commands, value);
            typeof(Monocle.Commands).GetField("charIndex", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(Engine.Commands, value.Length);
        }

        public void HandleDebugKeystroke(On.Monocle.Commands.orig_HandleKey orig, Monocle.Commands self, Keys key) {
            if (this.CaptureInput) {
                var extraState = Keyboard.GetState();
                var ctrl = extraState[Keys.LeftControl] == KeyState.Down || extraState[Keys.RightControl] == KeyState.Down;
                switch (key) {
                    case Keys.Enter:
                        var line = this.PopText();
                        this.HandleLine(line);
                        // do we actually want this behavior to be conditional...
                        if (this.HistoryIndex == this.History.Count || line != this.History[this.HistoryIndex]) {
                            this.History.Add(line);
                        }
                        this.HistoryIndex = this.History.Count;
                        return;
                    case Keys.C:
                        if (ctrl) {
                            this.PopText();
                            this.HandleCancel();
                            return;
                        }
                        break;
                    case Keys.D:
                        if (ctrl) {
                            this.SavedLine = this.PopText();
                            this.CaptureInput = false;
                            return;
                        }
                        break;
                    case Keys.Down:
                    case Keys.Up:
                        var dir = key == Keys.Up ? -1 : 1;
                        this.HistoryIndex = Calc.Clamp(this.HistoryIndex + dir, 0, this.History.Count);
                        this.SetText(this.HistoryIndex == this.History.Count ? "" : this.History[this.HistoryIndex]);
                        return;
                    case Keys.Tab:
                        // TODO
                        return;
                }
            }
            orig(self, key);
        }

        public void CustomPrompt(ILContext il) {
            var cursor = new ILCursor(il);
            while (cursor.TryGotoNext(MoveType.After, insn => insn.MatchLdstr(">"))) {
                cursor.EmitDelegate<Func<string, string>>(oldString => this.CaptureInput ? this.Prompt : oldString);
            }
        }
        #endregion

        public Evaluator Eval;
        public DebugWriter ErrPrinter;

        public void Setup() {
            this.ErrPrinter = new DebugWriter();
            var ctx = new CompilerContext(new CompilerSettings(), new StreamReportPrinter(this.ErrPrinter));
            this.Eval = new Evaluator(ctx);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { 
                this.Eval.ReferenceAssembly(asm);
            }

            this.ErrPrinter.Intercept = true;
            this.Eval.Run("using Celeste;");
            this.Eval.Run("using Monocle;");
            this.Eval.Run("using Microsoft.Xna.Framework;");
            this.Eval.Run("using Microsoft.Xna.Framework.Input;");
            this.Eval.Run("using System;");
            this.Eval.Run("using System.Collections;");
            this.Eval.Run("using System.Collections.Generic;");
            this.ErrPrinter.Intercept = false;
        }
        public void HandleLine(string line) {
            Engine.Commands.Log(line, Color.Aqua);
            try {
                var obj = this.Eval.Evaluate(line);
                if (obj == null) {
                    Engine.Commands.Log("null");
                } else if (obj is string objs) {
                    Engine.Commands.Log('"' + objs.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"');
                } else {
                    Engine.Commands.Log(obj.ToString());
                }
            } catch (Exception e) {
                //Engine.Commands.Log($"{e.GetType().Name}: {e.Message}", Color.Red);
                if (e.Message != "The expression failed to resolve") {
                    Engine.Commands.Log(e.Message, Color.Yellow);
                }
            }
        }

        public void HandleCancel() {
        }
    }
            
    public class DebugWriter : TextWriter {
        public override Encoding Encoding { get; }
        
        private List<char> Buffer = new List<char>();
        public Color Color = Color.Yellow;
        public bool Intercept = false;

        public override void Write(char value) {
            if (this.Intercept) {
                return;
            }
            if (value == '\n') {
                Engine.Commands.Log(new string (this.Buffer.ToArray()), this.Color);
                this.Buffer.Clear();
            } else {
                this.Buffer.Add(value);
            }
        }
    }
}

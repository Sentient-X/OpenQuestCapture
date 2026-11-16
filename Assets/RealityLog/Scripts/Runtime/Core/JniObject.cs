# nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RealityLog.Common
{
    /// <summary>
    /// The JNI descriptors OQC calls on one Java class. One table per class, the only place a
    /// method name meets its descriptor: <see cref="JniObject.Bind"/> resolves every entry on
    /// Unity's main thread, and scripts/check_jni_abi.py holds the same tables to the shipped
    /// AAR's classes on the host before any build is archived.
    /// </summary>
    public sealed class JniSignatures
    {
        public string ClassName { get; }
        public IReadOnlyList<(string name, string descriptor)> Methods { get; }

        public JniSignatures(string className, (string name, string descriptor)[] methods)
        {
            ClassName = className;
            Methods = methods;
        }
    }

    /// <summary>A Java call that threw, carrying the Java stack (with causes) as its message.</summary>
    public sealed class JavaCallException : Exception
    {
        public JavaCallException(string message) : base(message) { }
    }

    /// <summary>
    /// A Java object called through method ids resolved once, on Unity's main thread, from a
    /// <see cref="JniSignatures"/> table (sx#4210).
    ///
    /// pod5 2026-11-16, build 75: QuestCameraLib's stopRecording compiled to ()Lkotlin/Unit;, and
    /// OQC's AndroidJavaObject.Call("stopRecording") looked up ()V for the first time at the first
    /// stop, on the stop worker; both eyes threw NoSuchMethodError and no MP4 was finalized. Bound
    /// here, the same mismatch fails at the recorder's creation, on the main thread, and refuses
    /// the start. AndroidJavaObject also resolves each call through Unity's reflection helper,
    /// which on a worker thread is not reliable (HttpServerController's clock note); a call here
    /// is one raw JNI call on a global reference, so any thread attached to the JVM
    /// (<see cref="AndroidJNI.AttachCurrentThread"/>) may make it.
    /// </summary>
    public sealed class JniObject : IDisposable
    {
        private readonly string className;
        private readonly Dictionary<string, IntPtr> methods;
        private IntPtr globalRef;

        private static IntPtr logClass;
        private static IntPtr stackTraceString;

        private JniObject(string className, IntPtr globalRef, Dictionary<string, IntPtr> methods)
        {
            this.className = className;
            this.globalRef = globalRef;
            this.methods = methods;
        }

        /// <summary>
        /// Main thread. Resolves every method of <paramref name="signatures"/> on
        /// <paramref name="instance"/>'s class; throws <see cref="JavaCallException"/> naming each
        /// one the class lacks (the JVM's NoSuchMethodError text), so a library/app mismatch is
        /// found where the object is made, not where the call first runs.
        /// </summary>
        public static JniObject Bind(AndroidJavaObject instance, JniSignatures signatures)
        {
            EnsureStackTraceHelper();
            var clazz = instance.GetRawClass();
            var resolved = new Dictionary<string, IntPtr>();
            var missing = new List<string>();
            foreach (var (name, descriptor) in signatures.Methods)
            {
                IntPtr id = IntPtr.Zero;
                try { id = AndroidJNI.GetMethodID(clazz, name, descriptor); }
                catch (Exception ex) { missing.Add($"{name}{descriptor}: {ex.Message}"); continue; }
                var thrown = TakePendingException();
                if (thrown != null || id == IntPtr.Zero)
                {
                    missing.Add($"{name}{descriptor}: {thrown ?? "not found"}");
                    continue;
                }
                resolved[Key(name, descriptor)] = id;
            }
            if (missing.Count > 0)
            {
                throw new JavaCallException(
                    $"{signatures.ClassName} lacks {missing.Count} method(s) OQC calls (an AAR/app JNI mismatch): " +
                    string.Join("; ", missing));
            }
            return new JniObject(signatures.ClassName, AndroidJNI.NewGlobalRef(instance.GetRawObject()), resolved);
        }

        public void CallVoid(string name, string descriptor, params object[] args)
        {
            var (values, locals) = Args(args);
            try
            {
                AndroidJNI.CallVoidMethod(Ref, Method(name, descriptor), values);
                ThrowPending(name);
            }
            finally { DeleteLocals(locals); }
        }

        public long CallLong(string name, string descriptor)
        {
            var value = AndroidJNI.CallLongMethod(Ref, Method(name, descriptor), Array.Empty<jvalue>());
            ThrowPending(name);
            return value;
        }

        /// <summary>A String-returning call; null for a Java null.</summary>
        public string? CallString(string name, string descriptor, params object[] args)
        {
            var (values, locals) = Args(args);
            try
            {
                var result = AndroidJNI.CallObjectMethod(Ref, Method(name, descriptor), values);
                ThrowPending(name);
                if (result == IntPtr.Zero) return null;
                try { return AndroidJNI.GetStringChars(result); }
                finally { AndroidJNI.DeleteLocalRef(result); }
            }
            finally { DeleteLocals(locals); }
        }

        public void Dispose()
        {
            if (globalRef == IntPtr.Zero) return;
            AndroidJNI.DeleteGlobalRef(globalRef);
            globalRef = IntPtr.Zero;
        }

        private IntPtr Ref => globalRef != IntPtr.Zero
            ? globalRef
            : throw new ObjectDisposedException($"JniObject({className})");

        private IntPtr Method(string name, string descriptor) =>
            methods.TryGetValue(Key(name, descriptor), out var id)
                ? id
                : throw new InvalidOperationException($"{className}.{name}{descriptor} is not in its JniSignatures table");

        private static string Key(string name, string descriptor) => name + descriptor;

        private static (jvalue[] values, List<IntPtr> locals) Args(object[] args)
        {
            var locals = new List<IntPtr>();
            var values = args.Select(arg =>
            {
                switch (arg)
                {
                    case string s:
                        var js = AndroidJNI.NewString(s);
                        locals.Add(js);
                        return new jvalue { l = js };
                    case long j: return new jvalue { j = j };
                    case int i: return new jvalue { i = i };
                    case bool z: return new jvalue { z = z };
                    default: throw new ArgumentException($"unsupported JNI argument {arg?.GetType().Name ?? "null"}");
                }
            }).ToArray();
            return (values, locals);
        }

        private static void DeleteLocals(List<IntPtr> locals)
        {
            foreach (var local in locals) AndroidJNI.DeleteLocalRef(local);
        }

        private void ThrowPending(string name)
        {
            var thrown = TakePendingException();
            if (thrown != null) throw new JavaCallException($"{className}.{name} threw: {thrown}");
        }

        /// <summary>Clears a pending Java exception and returns its full stack, or null.</summary>
        private static string? TakePendingException()
        {
            var throwable = AndroidJNI.ExceptionOccurred();
            if (throwable == IntPtr.Zero) return null;
            AndroidJNI.ExceptionClear();
            try
            {
                if (logClass == IntPtr.Zero || stackTraceString == IntPtr.Zero) return "a Java exception (no stack helper)";
                var text = AndroidJNI.CallStaticObjectMethod(logClass, stackTraceString, new[] { new jvalue { l = throwable } });
                if (AndroidJNI.ExceptionOccurred() != IntPtr.Zero)
                {
                    AndroidJNI.ExceptionClear();
                    return "a Java exception (its stack could not be read)";
                }
                if (text == IntPtr.Zero) return "a Java exception";
                try { return AndroidJNI.GetStringChars(text).Trim(); }
                finally { AndroidJNI.DeleteLocalRef(text); }
            }
            finally
            {
                AndroidJNI.DeleteLocalRef(throwable);
            }
        }

        /// <summary>Main thread, once: android.util.Log.getStackTraceString, held globally.</summary>
        private static void EnsureStackTraceHelper()
        {
            if (logClass != IntPtr.Zero) return;
            var local = AndroidJNI.FindClass("android/util/Log");
            if (local == IntPtr.Zero) { AndroidJNI.ExceptionClear(); return; }
            var id = AndroidJNI.GetStaticMethodID(local, "getStackTraceString", "(Ljava/lang/Throwable;)Ljava/lang/String;");
            if (AndroidJNI.ExceptionOccurred() != IntPtr.Zero) { AndroidJNI.ExceptionClear(); id = IntPtr.Zero; }
            stackTraceString = id;
            logClass = AndroidJNI.NewGlobalRef(local);
            AndroidJNI.DeleteLocalRef(local);
        }
    }
}

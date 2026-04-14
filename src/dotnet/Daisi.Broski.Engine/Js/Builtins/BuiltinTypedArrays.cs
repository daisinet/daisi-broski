namespace Daisi.Broski.Engine.Js;

/// <summary>
/// <c>ArrayBuffer</c>, the nine numeric typed arrays, and
/// <c>DataView</c>. Each TypedArray constructor supports the
/// three canonical forms:
///
/// <list type="bullet">
/// <item><c>new T(length)</c> — allocates a fresh
///   <see cref="JsArrayBuffer"/> of <c>length *
///   BYTES_PER_ELEMENT</c> bytes and views it.</item>
/// <item><c>new T(iterableOrArray)</c> — consumes an
///   iterable (via the slice 3b-7a protocol) and copies
///   each element into a fresh buffer.</item>
/// <item><c>new T(buffer, byteOffset?, length?)</c> —
///   aliases an existing buffer, producing a view that
///   shares the same bytes.</item>
/// </list>
///
/// Each typed array shares a prototype that exposes the
/// common read/write + iteration surface. Callback-taking
/// methods (<c>forEach</c>, <c>map</c>, <c>reduce</c>,
/// <c>filter</c>, etc.) use <see cref="JsFunction.NativeCallable"/>
/// so they can invoke user functions via the re-entrant VM.
/// </summary>
internal static class BuiltinTypedArrays
{
    public static void Install(JsEngine engine)
    {
        InstallArrayBuffer(engine);
        InstallTypedArrayFamily(engine);
        InstallDataView(engine);
    }

    // ------------------------------------------------------
    // ArrayBuffer
    // ------------------------------------------------------

    private static void InstallArrayBuffer(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };
        proto.SetNonEnumerable("slice", new JsFunction("slice", (thisVal, args) =>
        {
            if (thisVal is not JsArrayBuffer buf)
            {
                JsThrow.TypeError("ArrayBuffer.prototype.slice called on non-ArrayBuffer");
            }
            var b = (JsArrayBuffer)thisVal!;
            int start = args.Count > 0 ? ResolveSliceIndex(args[0], b.ByteLength, 0) : 0;
            int end = args.Count > 1 && args[1] is not JsUndefined
                ? ResolveSliceIndex(args[1], b.ByteLength, b.ByteLength)
                : b.ByteLength;
            if (end < start) end = start;
            var copy = new JsArrayBuffer(end - start) { Prototype = proto };
            System.Array.Copy(b.Data, start, copy.Data, 0, end - start);
            return copy;
        }));

        var ctor = new JsFunction("ArrayBuffer", (thisVal, args) =>
        {
            int len = args.Count > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            return new JsArrayBuffer(len) { Prototype = proto };
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["ArrayBuffer"] = ctor;
    }

    // ------------------------------------------------------
    // Typed arrays — installed as a family sharing most
    // method implementations, parameterized by element kind.
    // ------------------------------------------------------

    private static void InstallTypedArrayFamily(JsEngine engine)
    {
        InstallTypedArray(engine, "Int8Array", TypedArrayKind.Int8);
        InstallTypedArray(engine, "Uint8Array", TypedArrayKind.Uint8);
        InstallTypedArray(engine, "Uint8ClampedArray", TypedArrayKind.Uint8Clamped);
        InstallTypedArray(engine, "Int16Array", TypedArrayKind.Int16);
        InstallTypedArray(engine, "Uint16Array", TypedArrayKind.Uint16);
        InstallTypedArray(engine, "Int32Array", TypedArrayKind.Int32);
        InstallTypedArray(engine, "Uint32Array", TypedArrayKind.Uint32);
        InstallTypedArray(engine, "Float32Array", TypedArrayKind.Float32);
        InstallTypedArray(engine, "Float64Array", TypedArrayKind.Float64);
    }

    private static void InstallTypedArray(JsEngine engine, string globalName, TypedArrayKind kind)
    {
        int elementSize = JsTypedArray.ElementSizeFor(kind);
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        proto.SetNonEnumerable("set", new JsFunction("set", (vm, thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.set");
            int offset = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            var source = args.Count > 0 ? args[0] : JsValue.Undefined;
            int i = 0;
            if (source is JsArray srcA)
            {
                for (; i < srcA.Elements.Count; i++)
                {
                    arr.WriteElement(offset + i, srcA.Elements[i]);
                }
                return JsValue.Undefined;
            }
            if (source is JsTypedArray srcT)
            {
                for (; i < srcT.Length; i++)
                {
                    arr.WriteElement(offset + i, srcT.ReadElement(i));
                }
                return JsValue.Undefined;
            }
            // Fall back to iterator protocol.
            var iter = vm.GetIteratorFromIterable(source);
            if (iter is not JsObject iterObj) return JsValue.Undefined;
            var nextFn = iterObj.Get("next") as JsFunction;
            if (nextFn is null) return JsValue.Undefined;
            while (true)
            {
                var step = vm.InvokeJsFunction(nextFn, iterObj, System.Array.Empty<object?>());
                if (step is not JsObject stepObj) break;
                if (JsValue.ToBoolean(stepObj.Get("done"))) break;
                arr.WriteElement(offset + i++, stepObj.Get("value"));
            }
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("subarray", new JsFunction("subarray", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.subarray");
            int start = args.Count > 0 ? ResolveSliceIndex(args[0], arr.Length, 0) : 0;
            int end = args.Count > 1 && args[1] is not JsUndefined
                ? ResolveSliceIndex(args[1], arr.Length, arr.Length)
                : arr.Length;
            if (end < start) end = start;
            int subLength = end - start;
            return new JsTypedArray(arr.Kind, arr.Buffer, arr.ByteOffset + start * arr.ElementSize, subLength)
            { Prototype = proto };
        }));

        proto.SetNonEnumerable("slice", new JsFunction("slice", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.slice");
            int start = args.Count > 0 ? ResolveSliceIndex(args[0], arr.Length, 0) : 0;
            int end = args.Count > 1 && args[1] is not JsUndefined
                ? ResolveSliceIndex(args[1], arr.Length, arr.Length)
                : arr.Length;
            if (end < start) end = start;
            int len = end - start;
            var buf = new JsArrayBuffer(len * arr.ElementSize);
            var copy = new JsTypedArray(arr.Kind, buf, 0, len) { Prototype = proto };
            for (int i = 0; i < len; i++)
            {
                copy.WriteElement(i, arr.ReadElement(start + i));
            }
            return copy;
        }));

        proto.SetNonEnumerable("fill", new JsFunction("fill", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.fill");
            object? value = args.Count > 0 ? args[0] : JsValue.Undefined;
            int start = args.Count > 1 ? ResolveSliceIndex(args[1], arr.Length, 0) : 0;
            int end = args.Count > 2 && args[2] is not JsUndefined
                ? ResolveSliceIndex(args[2], arr.Length, arr.Length)
                : arr.Length;
            for (int i = start; i < end; i++) arr.WriteElement(i, value);
            return arr;
        }));

        proto.SetNonEnumerable("indexOf", new JsFunction("indexOf", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.indexOf");
            object? target = args.Count > 0 ? args[0] : JsValue.Undefined;
            int from = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            if (from < 0) from = System.Math.Max(0, arr.Length + from);
            for (int i = from; i < arr.Length; i++)
            {
                if (JsValue.StrictEquals(arr.ReadElement(i), target)) return (double)i;
            }
            return -1.0;
        }));

        proto.SetNonEnumerable("join", new JsFunction("join", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.join");
            string sep = args.Count > 0 && args[0] is not JsUndefined
                ? JsValue.ToJsString(args[0])
                : ",";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                var v = arr.ReadElement(i);
                if (v is not JsUndefined && v is not JsNull)
                {
                    sb.Append(JsValue.ToJsString(v));
                }
            }
            return sb.ToString();
        }));

        proto.SetNonEnumerable("toString", new JsFunction("toString", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.toString");
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsValue.ToJsString(arr.ReadElement(i)));
            }
            return sb.ToString();
        }));

        // Callback-taking methods — require the re-entrant VM.
        proto.SetNonEnumerable("forEach", new JsFunction("forEach", (vm, thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.forEach");
            if (args.Count == 0 || args[0] is not JsFunction)
            {
                JsThrow.TypeError($"{globalName}.prototype.forEach callback is not a function");
            }
            var cb = (JsFunction)args[0]!;
            for (int i = 0; i < arr.Length; i++)
            {
                vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { arr.ReadElement(i), (double)i, arr });
            }
            return JsValue.Undefined;
        }));

        proto.SetNonEnumerable("map", new JsFunction("map", (vm, thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.map");
            if (args.Count == 0 || args[0] is not JsFunction)
            {
                JsThrow.TypeError($"{globalName}.prototype.map callback is not a function");
            }
            var cb = (JsFunction)args[0]!;
            var buf = new JsArrayBuffer(arr.Length * elementSize);
            var result = new JsTypedArray(kind, buf, 0, arr.Length) { Prototype = proto };
            for (int i = 0; i < arr.Length; i++)
            {
                var mapped = vm.InvokeJsFunction(cb, JsValue.Undefined, new object?[] { arr.ReadElement(i), (double)i, arr });
                result.WriteElement(i, mapped);
            }
            return result;
        }));

        proto.SetNonEnumerable("reduce", new JsFunction("reduce", (vm, thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.reduce");
            if (args.Count == 0 || args[0] is not JsFunction)
            {
                JsThrow.TypeError($"{globalName}.prototype.reduce callback is not a function");
            }
            var cb = (JsFunction)args[0]!;
            object? acc;
            int start;
            if (args.Count > 1)
            {
                acc = args[1];
                start = 0;
            }
            else
            {
                if (arr.Length == 0)
                {
                    JsThrow.TypeError("Reduce of empty typed array with no initial value");
                }
                acc = arr.ReadElement(0);
                start = 1;
            }
            for (int i = start; i < arr.Length; i++)
            {
                acc = vm.InvokeJsFunction(cb, JsValue.Undefined,
                    new object?[] { acc, arr.ReadElement(i), (double)i, arr });
            }
            return acc;
        }));

        // Iterator protocol: values() returns a fresh
        // iterator; [Symbol.iterator] defaults to values()
        // per spec. Keys / entries are deferred.
        proto.SetNonEnumerable("values", new JsFunction("values", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype.values");
            return CreateTypedArrayIterator(engine, arr);
        }));
        proto.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (thisVal, args) =>
        {
            var arr = RequireTypedArray(thisVal, $"{globalName}.prototype[Symbol.iterator]");
            return CreateTypedArrayIterator(engine, arr);
        }));

        // Constructor.
        var ctor = new JsFunction(globalName, (vm, thisVal, args) =>
        {
            // Three constructor forms:
            //   1. new T(buffer, byteOffset?, length?)
            //   2. new T(length)      — numeric arg
            //   3. new T(iterable)    — anything else
            if (args.Count == 0)
            {
                var buf0 = new JsArrayBuffer(0);
                return new JsTypedArray(kind, buf0, 0, 0) { Prototype = proto };
            }
            if (args[0] is JsArrayBuffer existing)
            {
                int byteOffset = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
                int length;
                if (args.Count > 2 && args[2] is not JsUndefined)
                {
                    length = (int)JsValue.ToNumber(args[2]);
                }
                else
                {
                    length = (existing.ByteLength - byteOffset) / elementSize;
                }
                return new JsTypedArray(kind, existing, byteOffset, length) { Prototype = proto };
            }
            if (args[0] is double numericLen)
            {
                int len = (int)numericLen;
                var buf = new JsArrayBuffer(len * elementSize);
                return new JsTypedArray(kind, buf, 0, len) { Prototype = proto };
            }
            // Iterable / array-like — materialize the source
            // into a list, allocate, and populate.
            var items = CollectIterableOrArrayLike(vm, args[0]);
            var buf2 = new JsArrayBuffer(items.Count * elementSize);
            var arr2 = new JsTypedArray(kind, buf2, 0, items.Count) { Prototype = proto };
            for (int i = 0; i < items.Count; i++) arr2.WriteElement(i, items[i]);
            return arr2;
        });
        ctor.SetNonEnumerable("prototype", proto);
        ctor.SetNonEnumerable("BYTES_PER_ELEMENT", (double)elementSize);
        proto.SetNonEnumerable("constructor", ctor);
        proto.SetNonEnumerable("BYTES_PER_ELEMENT", (double)elementSize);

        // ES2015 statics. TypedArray.from(source, mapFn?)
        // materializes an iterable or array-like into a
        // fresh typed array of the constructor's kind.
        // TypedArray.of(...items) is the var-args form.
        ctor.SetNonEnumerable("from", new JsFunction("from", (vm, thisVal, args) =>
        {
            object? source = args.Count > 0 ? args[0] : JsValue.Undefined;
            JsFunction? mapFn = args.Count > 1 && args[1] is JsFunction m ? m : null;
            var items = CollectIterableOrArrayLike(vm, source);
            var buf = new JsArrayBuffer(items.Count * elementSize);
            var arr = new JsTypedArray(kind, buf, 0, items.Count) { Prototype = proto };
            for (int i = 0; i < items.Count; i++)
            {
                object? v = items[i];
                if (mapFn is not null)
                {
                    v = vm.InvokeJsFunction(mapFn, JsValue.Undefined, new object?[] { v, (double)i });
                }
                arr.WriteElement(i, v);
            }
            return arr;
        }));
        ctor.SetNonEnumerable("of", new JsFunction("of", (thisVal, args) =>
        {
            var buf = new JsArrayBuffer(args.Count * elementSize);
            var arr = new JsTypedArray(kind, buf, 0, args.Count) { Prototype = proto };
            for (int i = 0; i < args.Count; i++) arr.WriteElement(i, args[i]);
            return arr;
        }));

        engine.Globals[globalName] = ctor;
    }

    private static JsTypedArray RequireTypedArray(object? thisVal, string name)
    {
        if (thisVal is not JsTypedArray arr)
        {
            JsThrow.TypeError($"{name} called on non-typed-array");
        }
        return (JsTypedArray)thisVal!;
    }

    private static JsObject CreateTypedArrayIterator(JsEngine engine, JsTypedArray arr)
    {
        int index = 0;
        var iter = new JsObject { Prototype = engine.ObjectPrototype };
        iter.SetNonEnumerable("next", new JsFunction("next", (t, a) =>
        {
            var result = new JsObject { Prototype = engine.ObjectPrototype };
            if (index >= arr.Length)
            {
                result.Set("value", JsValue.Undefined);
                result.Set("done", JsValue.True);
                return result;
            }
            result.Set("value", arr.ReadElement(index++));
            result.Set("done", JsValue.False);
            return result;
        }));
        iter.SetSymbol(engine.IteratorSymbol, new JsFunction("[Symbol.iterator]", (t, a) => iter));
        return iter;
    }

    private static List<object?> CollectIterableOrArrayLike(JsVM vm, object? source)
    {
        var list = new List<object?>();
        if (source is JsArray srcArr)
        {
            foreach (var e in srcArr.Elements) list.Add(e);
            return list;
        }
        // Array-like: has a `length` and integer-indexed props.
        if (source is JsObject jo)
        {
            var iter = vm.GetIteratorFromIterable(source);
            if (iter is JsObject iterObj)
            {
                var nextFn = iterObj.Get("next") as JsFunction;
                if (nextFn is not null)
                {
                    while (true)
                    {
                        var step = vm.InvokeJsFunction(nextFn, iterObj, System.Array.Empty<object?>());
                        if (step is not JsObject stepObj) break;
                        if (JsValue.ToBoolean(stepObj.Get("done"))) break;
                        list.Add(stepObj.Get("value"));
                    }
                    return list;
                }
            }
            // Fall back: array-like with numeric length.
            var lenVal = jo.Get("length");
            int len = (int)JsValue.ToNumber(lenVal);
            for (int i = 0; i < len; i++)
            {
                list.Add(jo.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return list;
    }

    /// <summary>
    /// Resolve an absolute index from a spec-style relative
    /// one: negative values count from the end; out-of-range
    /// values clamp to <c>[0, length]</c>.
    /// </summary>
    private static int ResolveSliceIndex(object? value, int length, int fallback)
    {
        if (value is JsUndefined) return fallback;
        int n = (int)JsValue.ToNumber(value);
        if (n < 0) n = System.Math.Max(0, length + n);
        if (n > length) n = length;
        return n;
    }

    // ------------------------------------------------------
    // DataView
    // ------------------------------------------------------

    private static void InstallDataView(JsEngine engine)
    {
        var proto = new JsObject { Prototype = engine.ObjectPrototype };

        // Bounds-checking helper — spec-compliant accesses
        // that fall outside [0, byteLength - byteCount] must
        // raise a RangeError, not leak a .NET
        // IndexOutOfRangeException. Blazor's minified runtime
        // probes DataView shape with speculative offsets and
        // relies on the RangeError to fall through to a
        // fallback path.
        static void CheckRange(JsDataView dv, int off, int bytes, string name)
        {
            if (off < 0 || off + bytes > dv.ByteLength)
            {
                JsThrow.RangeError(
                    $"DataView.prototype.{name} offset {off} is out of bounds");
            }
        }

        proto.SetNonEnumerable("getInt8", new JsFunction("getInt8", (thisVal, args) =>
        {
            var dv = RequireDataView(thisVal, "getInt8");
            int off = (int)JsValue.ToNumber(args.Count > 0 ? args[0] : 0.0);
            CheckRange(dv, off, 1, "getInt8");
            return (double)(sbyte)dv.Buffer.Data[dv.ByteOffset + off];
        }));
        proto.SetNonEnumerable("setInt8", new JsFunction("setInt8", (thisVal, args) =>
        {
            var dv = RequireDataView(thisVal, "setInt8");
            int off = (int)JsValue.ToNumber(args.Count > 0 ? args[0] : 0.0);
            CheckRange(dv, off, 1, "setInt8");
            int v = JsValue.ToInt32(args.Count > 1 ? args[1] : 0.0);
            dv.Buffer.Data[dv.ByteOffset + off] = unchecked((byte)(sbyte)v);
            return JsValue.Undefined;
        }));
        proto.SetNonEnumerable("getUint8", new JsFunction("getUint8", (thisVal, args) =>
        {
            var dv = RequireDataView(thisVal, "getUint8");
            int off = (int)JsValue.ToNumber(args.Count > 0 ? args[0] : 0.0);
            CheckRange(dv, off, 1, "getUint8");
            return (double)dv.Buffer.Data[dv.ByteOffset + off];
        }));
        proto.SetNonEnumerable("setUint8", new JsFunction("setUint8", (thisVal, args) =>
        {
            var dv = RequireDataView(thisVal, "setUint8");
            int off = (int)JsValue.ToNumber(args.Count > 0 ? args[0] : 0.0);
            CheckRange(dv, off, 1, "setUint8");
            uint v = (uint)JsValue.ToUint32(args.Count > 1 ? args[1] : 0.0);
            dv.Buffer.Data[dv.ByteOffset + off] = (byte)v;
            return JsValue.Undefined;
        }));

        InstallMultiByteGetter(proto, "getInt16", 2, (bytes) => (double)System.BitConverter.ToInt16(bytes, 0));
        InstallMultiByteGetter(proto, "getUint16", 2, (bytes) => (double)System.BitConverter.ToUInt16(bytes, 0));
        InstallMultiByteGetter(proto, "getInt32", 4, (bytes) => (double)System.BitConverter.ToInt32(bytes, 0));
        InstallMultiByteGetter(proto, "getUint32", 4, (bytes) => (double)System.BitConverter.ToUInt32(bytes, 0));
        InstallMultiByteGetter(proto, "getFloat32", 4, (bytes) => (double)System.BitConverter.ToSingle(bytes, 0));
        InstallMultiByteGetter(proto, "getFloat64", 8, (bytes) => System.BitConverter.ToDouble(bytes, 0));

        InstallMultiByteSetter(proto, "setInt16", 2, (v) => System.BitConverter.GetBytes((short)JsValue.ToInt32(v)));
        InstallMultiByteSetter(proto, "setUint16", 2, (v) => System.BitConverter.GetBytes((ushort)(uint)JsValue.ToUint32(v)));
        InstallMultiByteSetter(proto, "setInt32", 4, (v) => System.BitConverter.GetBytes(JsValue.ToInt32(v)));
        InstallMultiByteSetter(proto, "setUint32", 4, (v) => System.BitConverter.GetBytes((uint)JsValue.ToUint32(v)));
        InstallMultiByteSetter(proto, "setFloat32", 4, (v) => System.BitConverter.GetBytes((float)JsValue.ToNumber(v)));
        InstallMultiByteSetter(proto, "setFloat64", 8, (v) => System.BitConverter.GetBytes(JsValue.ToNumber(v)));

        var ctor = new JsFunction("DataView", (thisVal, args) =>
        {
            if (args.Count == 0 || args[0] is not JsArrayBuffer buf)
            {
                JsThrow.TypeError("DataView constructor argument is not an ArrayBuffer");
            }
            var b = (JsArrayBuffer)args[0]!;
            int byteOffset = args.Count > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            int byteLength;
            if (args.Count > 2 && args[2] is not JsUndefined)
            {
                byteLength = (int)JsValue.ToNumber(args[2]);
            }
            else
            {
                byteLength = b.ByteLength - byteOffset;
            }
            return new JsDataView(b, byteOffset, byteLength) { Prototype = proto };
        });
        ctor.SetNonEnumerable("prototype", proto);
        proto.SetNonEnumerable("constructor", ctor);
        engine.Globals["DataView"] = ctor;
    }

    private static void InstallMultiByteGetter(
        JsObject proto,
        string name,
        int byteCount,
        System.Func<byte[], double> decode)
    {
        proto.SetNonEnumerable(name, new JsFunction(name, (thisVal, args) =>
        {
            var dv = RequireDataView(thisVal, name);
            int off = (int)JsValue.ToNumber(args.Count > 0 ? args[0] : 0.0);
            bool littleEndian = args.Count > 1 && JsValue.ToBoolean(args[1]);
            var bytes = dv.ReadBytes(off, byteCount, littleEndian);
            return decode(bytes);
        }));
    }

    private static void InstallMultiByteSetter(
        JsObject proto,
        string name,
        int byteCount,
        System.Func<object?, byte[]> encode)
    {
        proto.SetNonEnumerable(name, new JsFunction(name, (thisVal, args) =>
        {
            var dv = RequireDataView(thisVal, name);
            int off = (int)JsValue.ToNumber(args.Count > 0 ? args[0] : 0.0);
            object? value = args.Count > 1 ? args[1] : JsValue.Undefined;
            bool littleEndian = args.Count > 2 && JsValue.ToBoolean(args[2]);
            var bytes = encode(value);
            dv.WriteBytes(off, bytes, littleEndian);
            return JsValue.Undefined;
        }));
    }

    private static JsDataView RequireDataView(object? thisVal, string name)
    {
        if (thisVal is not JsDataView dv)
        {
            JsThrow.TypeError($"DataView.prototype.{name} called on non-DataView");
        }
        return (JsDataView)thisVal!;
    }
}

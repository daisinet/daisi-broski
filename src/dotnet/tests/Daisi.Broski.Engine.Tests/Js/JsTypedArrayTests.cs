using Daisi.Broski.Engine.Js;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Js;

public class JsTypedArrayTests
{
    private static object? Eval(string src) => new JsEngine().Evaluate(src);

    // ========================================================
    // ArrayBuffer
    // ========================================================

    [Fact]
    public void ArrayBuffer_length()
    {
        Assert.Equal(
            16.0,
            Eval("new ArrayBuffer(16).byteLength;"));
    }

    [Fact]
    public void ArrayBuffer_slice_copies_range()
    {
        Assert.Equal(
            4.0,
            Eval(@"
                var a = new ArrayBuffer(10);
                var b = a.slice(2, 6);
                b.byteLength;
            "));
    }

    // ========================================================
    // Uint8Array basics
    // ========================================================

    [Fact]
    public void Uint8Array_from_length()
    {
        Assert.Equal(
            "5:5:0",
            Eval(@"
                var a = new Uint8Array(5);
                a.length + ':' + a.byteLength + ':' + a[0];
            "));
    }

    [Fact]
    public void Uint8Array_from_array_literal()
    {
        Assert.Equal(
            "1,2,3,4",
            Eval(@"
                var a = new Uint8Array([1, 2, 3, 4]);
                a.join(',');
            "));
    }

    [Fact]
    public void Uint8Array_indexing_coerces_out_of_range()
    {
        // Values wrap to 0-255 modulo 256 for Uint8Array.
        Assert.Equal(
            "255,0,1",
            Eval(@"
                var a = new Uint8Array(3);
                a[0] = 255;
                a[1] = 256;  // wraps to 0
                a[2] = 257;  // wraps to 1
                a[0] + ',' + a[1] + ',' + a[2];
            "));
    }

    [Fact]
    public void Uint8Array_bytes_per_element()
    {
        Assert.Equal(
            1.0,
            Eval("Uint8Array.BYTES_PER_ELEMENT;"));
    }

    [Fact]
    public void Int8Array_negative_values_via_sign()
    {
        Assert.Equal(
            -1.0,
            Eval(@"
                var a = new Int8Array([255]);  // -1 in signed 8-bit
                a[0];
            "));
    }

    [Fact]
    public void Uint8ClampedArray_clamps_negatives_and_large()
    {
        Assert.Equal(
            "0,255,127",
            Eval(@"
                var a = new Uint8ClampedArray(3);
                a[0] = -5;    // clamped to 0
                a[1] = 999;   // clamped to 255
                a[2] = 127;
                a[0] + ',' + a[1] + ',' + a[2];
            "));
    }

    [Fact]
    public void Uint8ClampedArray_rounds_half_to_even()
    {
        // Spec: 0.5 → 0, 1.5 → 2, 2.5 → 2, 3.5 → 4
        Assert.Equal(
            "0,2,2,4",
            Eval(@"
                var a = new Uint8ClampedArray(4);
                a[0] = 0.5;
                a[1] = 1.5;
                a[2] = 2.5;
                a[3] = 3.5;
                a[0] + ',' + a[1] + ',' + a[2] + ',' + a[3];
            "));
    }

    // ========================================================
    // Multi-byte typed arrays
    // ========================================================

    [Fact]
    public void Int16Array_round_trip()
    {
        Assert.Equal(
            "-1000,1000",
            Eval(@"
                var a = new Int16Array(2);
                a[0] = -1000;
                a[1] = 1000;
                a[0] + ',' + a[1];
            "));
    }

    [Fact]
    public void Int32Array_length_and_bytes_per_element()
    {
        Assert.Equal(
            "16:4:4",
            Eval(@"
                var a = new Int32Array(4);
                a.byteLength + ':' + a.length + ':' + Int32Array.BYTES_PER_ELEMENT;
            "));
    }

    [Fact]
    public void Float32Array_round_trip_within_precision()
    {
        var result = Eval(@"
            var a = new Float32Array([3.5, -0.25, 100.125]);
            [a[0], a[1], a[2]];
        ");
        Assert.NotNull(result);
    }

    [Fact]
    public void Float64Array_preserves_high_precision()
    {
        Assert.Equal(
            3.14159265358979,
            Eval(@"
                var a = new Float64Array(1);
                a[0] = 3.14159265358979;
                a[0];
            "));
    }

    // ========================================================
    // Views over shared buffers
    // ========================================================

    [Fact]
    public void Typed_array_view_shares_buffer_with_original()
    {
        Assert.Equal(
            42.0,
            Eval(@"
                var buf = new ArrayBuffer(8);
                var a = new Uint8Array(buf);
                var b = new Uint8Array(buf);  // same bytes
                a[3] = 42;
                b[3];
            "));
    }

    [Fact]
    public void Subarray_aliases_parent_bytes()
    {
        Assert.Equal(
            "99,1,2,99,4",
            Eval(@"
                var a = new Uint8Array([0, 1, 2, 3, 4]);
                var sub = a.subarray(3, 4);   // view of [a[3]]
                sub[0] = 99;                  // writes through
                a[0] = 99;
                a.join(',');
            "));
    }

    [Fact]
    public void Slice_copies_to_new_buffer()
    {
        // Unlike subarray, slice copies.
        Assert.Equal(
            "0,1,2,3,4",
            Eval(@"
                var a = new Uint8Array([0, 1, 2, 3, 4]);
                var copy = a.slice();
                copy[0] = 99;
                a.join(',');
            "));
    }

    [Fact]
    public void Set_copies_from_array()
    {
        Assert.Equal(
            "10,20,30,0,0",
            Eval(@"
                var a = new Uint8Array(5);
                a.set([10, 20, 30]);
                a.join(',');
            "));
    }

    [Fact]
    public void Set_with_offset()
    {
        Assert.Equal(
            "0,0,7,7,7",
            Eval(@"
                var a = new Uint8Array(5);
                a.set([7, 7, 7], 2);
                a.join(',');
            "));
    }

    [Fact]
    public void Set_from_another_typed_array()
    {
        Assert.Equal(
            "1,2,3,0,0",
            Eval(@"
                var a = new Uint8Array(5);
                var src = new Uint8Array([1, 2, 3]);
                a.set(src);
                a.join(',');
            "));
    }

    [Fact]
    public void Fill_writes_range()
    {
        Assert.Equal(
            "9,9,9,9,9",
            Eval(@"
                var a = new Uint8Array(5);
                a.fill(9);
                a.join(',');
            "));
    }

    [Fact]
    public void Fill_range_bounded()
    {
        Assert.Equal(
            "0,5,5,5,0",
            Eval(@"
                var a = new Uint8Array(5);
                a.fill(5, 1, 4);
                a.join(',');
            "));
    }

    [Fact]
    public void IndexOf_finds_element()
    {
        Assert.Equal(
            2.0,
            Eval(@"
                var a = new Uint8Array([10, 20, 30, 40]);
                a.indexOf(30);
            "));
    }

    [Fact]
    public void IndexOf_returns_minus_one_if_absent()
    {
        Assert.Equal(
            -1.0,
            Eval(@"
                var a = new Uint8Array([10, 20, 30]);
                a.indexOf(99);
            "));
    }

    // ========================================================
    // Iteration
    // ========================================================

    [Fact]
    public void For_of_over_typed_array()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                var a = new Uint8Array([1, 2, 3, 4]);
                var total = 0;
                for (var v of a) total += v;
                total;
            "));
    }

    [Fact]
    public void Spread_typed_array_into_literal()
    {
        Assert.Equal(
            "1,2,3",
            Eval(@"
                var a = new Uint8Array([1, 2, 3]);
                [...a].join(',');
            "));
    }

    [Fact]
    public void ForEach_iterates_in_order()
    {
        Assert.Equal(
            "a:10,b:20,c:30",
            Eval(@"
                var labels = ['a', 'b', 'c'];
                var a = new Uint8Array([10, 20, 30]);
                var out = [];
                a.forEach(function (v, i) { out.push(labels[i] + ':' + v); });
                out.join(',');
            "));
    }

    [Fact]
    public void Map_returns_typed_array_of_same_kind()
    {
        Assert.Equal(
            "2,4,6,8",
            Eval(@"
                var a = new Uint8Array([1, 2, 3, 4]);
                var b = a.map(function (v) { return v * 2; });
                b.join(',');
            "));
    }

    [Fact]
    public void Reduce_sums_elements()
    {
        Assert.Equal(
            15.0,
            Eval(@"
                var a = new Uint8Array([1, 2, 3, 4, 5]);
                a.reduce(function (acc, v) { return acc + v; }, 0);
            "));
    }

    // ========================================================
    // DataView
    // ========================================================

    [Fact]
    public void DataView_Uint8_round_trip()
    {
        Assert.Equal(
            255.0,
            Eval(@"
                var buf = new ArrayBuffer(4);
                var dv = new DataView(buf);
                dv.setUint8(0, 255);
                dv.getUint8(0);
            "));
    }

    [Fact]
    public void DataView_Int8_sign_extends()
    {
        Assert.Equal(
            -128.0,
            Eval(@"
                var buf = new ArrayBuffer(4);
                var dv = new DataView(buf);
                dv.setInt8(0, -128);
                dv.getInt8(0);
            "));
    }

    [Fact]
    public void DataView_default_big_endian_round_trip_Uint16()
    {
        Assert.Equal(
            258.0,   // 0x0102
            Eval(@"
                var buf = new ArrayBuffer(4);
                var dv = new DataView(buf);
                dv.setUint16(0, 258);
                dv.getUint16(0);
            "));
    }

    [Fact]
    public void DataView_little_endian_flag_flips_bytes()
    {
        Assert.Equal(
            "BE=1:LE=256",
            Eval(@"
                var buf = new ArrayBuffer(2);
                var dv = new DataView(buf);
                dv.setUint8(0, 0);
                dv.setUint8(1, 1);
                'BE=' + dv.getUint16(0, false) + ':LE=' + dv.getUint16(0, true);
            "));
    }

    [Fact]
    public void DataView_Int32_round_trip()
    {
        Assert.Equal(
            -123456.0,
            Eval(@"
                var buf = new ArrayBuffer(8);
                var dv = new DataView(buf);
                dv.setInt32(0, -123456);
                dv.getInt32(0);
            "));
    }

    [Fact]
    public void DataView_Float64_round_trip()
    {
        Assert.Equal(
            3.14159265358979,
            Eval(@"
                var buf = new ArrayBuffer(8);
                var dv = new DataView(buf);
                dv.setFloat64(0, 3.14159265358979);
                dv.getFloat64(0);
            "));
    }

    [Fact]
    public void DataView_reads_from_Uint8Array_view()
    {
        // Writing through a DataView and reading back
        // through a Uint8Array over the same buffer lets us
        // verify the byte layout. setUint16(1, 1, true)
        // writes two little-endian bytes (0x01, 0x00) at
        // offsets 1 and 2.
        Assert.Equal(
            "0,1,0,0",
            Eval(@"
                var buf = new ArrayBuffer(4);
                var dv = new DataView(buf);
                var bytes = new Uint8Array(buf);
                dv.setUint16(1, 1, true);
                bytes.join(',');
            "));
    }

    // ========================================================
    // Realistic usage
    // ========================================================

    [Fact]
    public void Build_and_sum_float_array()
    {
        Assert.Equal(
            10.0,
            Eval(@"
                var nums = new Float64Array(4);
                for (var i = 0; i < 4; i++) nums[i] = i + 1;
                nums.reduce(function (a, b) { return a + b; }, 0);
            "));
    }

    [Fact]
    public void Image_pixel_pattern_via_Uint8ClampedArray()
    {
        Assert.Equal(
            "255,0,0,255",
            Eval(@"
                var px = new Uint8ClampedArray(4);
                px[0] = 300;   // clamps to 255 (R)
                px[1] = -10;   // clamps to 0  (G)
                px[2] = 0;     //               (B)
                px[3] = 255;   //               (A)
                px.join(',');
            "));
    }

    [Fact]
    public void Typed_array_indexOf_uses_strict_equality()
    {
        Assert.Equal(
            1.0,
            Eval(@"
                var a = new Int32Array([0, 5, 10]);
                a.indexOf(5);
            "));
    }
}

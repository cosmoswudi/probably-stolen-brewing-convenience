#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Minimal .NET metadata reader / IL disassembler (dnfile + dncil)."""
import re
import sys
import dnfile
from dncil.cil.body import CilMethodBody
from dncil.cil.body.reader import CilMethodBodyReaderBase
from dncil.clr.token import Token

import os
DLL = os.environ.get("PS_DLL") or r"D:/SteamLibrary/steamapps/common/Probably Stolen/Probably Stolen_Data/Managed/Assembly-CSharp.dll"
PE = dnfile.dnPE(DLL)
MD = PE.net.mdtables

TD = list(MD.TypeDef.rows)
FR = list(MD.Field.rows)
MR = list(MD.MethodDef.rows)

_TD_NAME = {}
for rid, r in enumerate(TD, 1):
    _TD_NAME[rid] = (str(r.TypeNamespace or ""), str(r.TypeName or ""))
for r in MD.NestedClass.rows:
    inner, outer = r.NestedClass.row_index, r.EnclosingClass.row_index
    if inner in _TD_NAME and outer in _TD_NAME:
        ns, _ = _TD_NAME[outer]
        _TD_NAME[inner] = (ns, _TD_NAME[outer][1] + "/" + _TD_NAME[inner][1])


def td_full(rid):
    ns, n = _TD_NAME.get(rid, ("", f"<td{rid}>"))
    return (ns + "." + n) if ns else n


_TS_CACHE = {}


def cint(b, o):
    x = b[o]
    if x & 0x80 == 0:
        return x, o + 1
    if x & 0xC0 == 0x80:
        return ((x & 0x3F) << 8) | b[o + 1], o + 2
    return ((x & 0x1F) << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3], o + 4


PRIM = {0x01: "void", 0x02: "bool", 0x03: "char", 0x04: "sbyte", 0x05: "byte", 0x06: "short",
        0x07: "ushort", 0x08: "int", 0x09: "uint", 0x0A: "long", 0x0B: "ulong", 0x0C: "float",
        0x0D: "double", 0x0E: "string", 0x16: "TypedReference", 0x18: "IntPtr", 0x19: "UIntPtr",
        0x1C: "object"}


def tr_full(rid):
    r = MD.TypeRef.rows[rid - 1]
    ns, n = str(r.TypeNamespace or ""), str(r.TypeName or "")
    pre = ""
    sc = r.ResolutionScope
    tn = getattr(getattr(sc, "table", None), "name", "") if sc is not None else ""
    if tn == "AssemblyRef":
        pre = f"[{str(MD.AssemblyRef.rows[sc.row_index-1].Name)}]"
    elif tn == "TypeRef":
        pre = tr_full(sc.row_index) + "/"
        ns = ""
    return pre + ((ns + "." + n) if ns else n)


def tdor(coded):
    tag = coded & 3
    rid = coded >> 2
    if tag == 0:
        return td_full(rid)
    if tag == 1:
        return tr_full(rid)
    return typespec(rid)


def typespec(rid):
    if rid in _TS_CACHE:
        return _TS_CACHE[rid]
    _TS_CACHE[rid] = f"<spec{rid}>"
    _TS_CACHE[rid] = parse_type(MD.TypeSpec.rows[rid - 1].Signature.value, 0)[0]
    return _TS_CACHE[rid]


def parse_type(b, o):
    et = b[o]
    o += 1
    if et in PRIM:
        return PRIM[et], o
    if et in (0x11, 0x12):
        c, o = cint(b, o)
        return tdor(c), o
    if et == 0x0F:
        t, o = parse_type(b, o)
        return t + "*", o
    if et == 0x10:
        t, o = parse_type(b, o)
        return "ref " + t, o
    if et == 0x1D:
        t, o = parse_type(b, o)
        return t + "[]", o
    if et in (0x13, 0x1E):
        n, o = cint(b, o)
        return ("!" if et == 0x13 else "!!") + str(n), o
    if et == 0x15:
        base, o = parse_type(b, o)
        argc, o = cint(b, o)
        args = []
        for _ in range(argc):
            a, o = parse_type(b, o)
            args.append(a)
        return f"{base}<{', '.join(args)}>", o
    if et == 0x14:
        t, o = parse_type(b, o)
        rank, o = cint(b, o)
        n1, o = cint(b, o)
        for _ in range(n1):
            _, o = cint(b, o)
        n2, o = cint(b, o)
        for _ in range(n2):
            _, o = cint(b, o)
        return f"{t}[{','*(rank-1)}]", o
    if et in (0x1F, 0x20):
        _, o = cint(b, o)
        return parse_type(b, o)
    if et == 0x45:
        return parse_type(b, o)
    if et == 0x1B:
        return "fnptr", o
    return f"<et{et:02x}>", o


def m_sig(b):
    o = 0
    conv = b[o]
    o += 1
    if conv & 0x10:
        _, o = cint(b, o)
    n, o = cint(b, o)
    ret, o = parse_type(b, o)
    ps = []
    for _ in range(n):
        if o < len(b) and b[o] == 0x41:
            o += 1
        t, o = parse_type(b, o)
        ps.append(t)
    return ret, ps, conv


def f_sig(b):
    return parse_type(b, 1)[0]


FIELD_OWNER = {}
METHOD_OWNER = {}
for rid, r in enumerate(TD, 1):
    for x in r.FieldList:
        FIELD_OWNER[x.row_index] = rid
    for x in r.MethodList:
        METHOD_OWNER[x.row_index] = rid


_CONST = None


def const_of(frid):
    """raw bytes of the Constant row for field `frid` (dnfile strips the ElementType prefix)."""
    global _CONST
    if _CONST is None:
        _CONST = {}
        try:
            for c in MD.Constant.rows:
                par = c.Parent
                tn = getattr(getattr(par, "table", None), "name", "")
                if tn != "Field":
                    continue
                raw = c.Value
                _CONST[par.row_index] = raw.value if hasattr(raw, "value") else bytes(raw)
        except Exception:
            pass
    return _CONST.get(frid)


def decode_const(b, t):
    if b is None:
        return None
    if t == "string":
        try:
            return b.decode("utf-16-le")
        except Exception:
            return b
    if t == "bool":
        return bool(b[0])
    if t in ("int", "uint", "short", "ushort", "byte", "sbyte", "long", "ulong"):
        return int.from_bytes(b, "little", signed=t in ("int", "short", "sbyte", "long"))
    if t == "float":
        import struct as _s
        return _s.unpack("<f", b)[0]
    return b


def field_full(rid):
    o = FIELD_OWNER.get(rid)
    return f"{td_full(o)}::{str(FR[rid-1].Name)}" if o else str(FR[rid-1].Name)


def mdef_str(rid):
    r = MR[rid - 1]
    o = METHOD_OWNER.get(rid)
    try:
        ret, ps, _ = m_sig(r.Signature.value)
    except Exception:
        ret, ps = "?", []
    names = [str(getattr(p, "row", p).Name or "") for p in r.ParamList]
    args = ", ".join(f"{t} {n}".strip() for t, n in zip(ps, names + [""] * len(ps))) if ps else ""
    if o:
        return f"{td_full(o)}::{str(r.Name)}({args}) -> {ret}"
    return f"{str(r.Name)}({args}) -> {ret}"


def mref_str(rid):
    r = MD.MemberRef.rows[rid - 1]
    cls = r.Class
    tn = getattr(getattr(cls, "table", None), "name", "")
    if tn == "TypeDef":
        c = td_full(cls.row_index)
    elif tn == "TypeRef":
        c = tr_full(cls.row_index)
    elif tn == "TypeSpec":
        c = typespec(cls.row_index)
    else:
        c = "?"
    try:
        ret, ps, _ = m_sig(r.Signature.value)
        return f"{c}::{str(r.Name)}({', '.join(ps)}) -> {ret}"
    except Exception:
        return f"{c}::{str(r.Name)}"


TOK_TABLE = {0x01: "TypeRef", 0x02: "TypeDef", 0x04: "Field", 0x06: "MethodDef",
             0x0A: "MemberRef", 0x11: "StandAloneSig", 0x1B: "TypeSpec", 0x2B: "MethodSpec", 0x70: "String"}


def tok_parts(tok):
    v = getattr(tok, "value", None)
    if v is None:
        v = int(tok) if not isinstance(tok, str) else 0
    return TOK_TABLE.get((v >> 24) & 0xFF, f"tbl{(v>>24)&0xFF:02x}"), v & 0xFFFFFF


def local_types(tok):
    try:
        tn, rid = tok_parts(tok)
        if tn != "StandAloneSig":
            return []
        sig = MD.StandAloneSig.rows[rid - 1].Signature.value
        o = 0
        if sig[o] != 0x07:
            return []
        o += 1
        n, o = cint(sig, o)
        out = []
        for _ in range(n):
            t, o = parse_type(sig, o)
            out.append(t)
        return out
    except Exception:
        return []


def resolve(tok):
    try:
        tn, rid = tok_parts(tok)
        if tn == "Field":
            return field_full(rid)
        if tn == "MethodDef":
            return mdef_str(rid)
        if tn == "MemberRef":
            return mref_str(rid)
        if tn == "TypeRef":
            return tr_full(rid)
        if tn == "TypeDef":
            return td_full(rid)
        if tn == "TypeSpec":
            return typespec(rid)
        if tn == "MethodSpec":
            r = MD.MethodSpec.rows[rid - 1]
            m = r.Method
            mtn = getattr(getattr(m, "table", None), "name", "")
            base = mdef_str(m.row_index) if mtn == "MethodDef" else mref_str(m.row_index)
            sig = r.Instantiation.value
            o = 1
            n, o = cint(sig, o)
            args = []
            for _ in range(n):
                a, o = parse_type(sig, o)
                args.append(a)
            return base.split(" -> ")[0] + "<" + ", ".join(args) + ">" + " -> " + base.split(" -> ")[-1]
        if tn == "String":
            _us = PE.net.user_strings.get(rid)
            return repr(getattr(_us, "value", str(_us)))
    except Exception:
        pass
    return str(tok)


class R(CilMethodBodyReaderBase):
    def __init__(self, row):
        self.off = PE.get_offset_from_rva(row.Rva)

    def read(self, n):
        d = PE.get_data(PE.get_rva_from_offset(self.off), n)
        self.off += n
        return d

    def tell(self):
        return self.off

    def seek(self, o):
        self.off = o
        return self.off


def dump(rid, ind="  "):
    r = MR[rid - 1]
    if r.Rva == 0:
        print(f"{ind}// (no body)")
        return
    try:
        body = CilMethodBody(R(r))
    except Exception as e:
        print(f"{ind}// body error: {e}")
        return
    lt = local_types(body.local_var_sig_tok)
    if lt:
        print(f"{ind}// locals: " + ", ".join(f"V{i}:{t}" for i, t in enumerate(lt)))
    base = body.offset
    for ins in body.instructions:
        o = ins.operand
        nm = ins.opcode.name
        if o is None:
            print(f"{ind}IL_{ins.offset - base:04x}: {nm}")
        elif isinstance(o, Token):
            print(f"{ind}IL_{ins.offset - base:04x}: {nm} {resolve(o)}")
        elif isinstance(o, int) and (nm.startswith("b") or nm.startswith("leave")):
            print(f"{ind}IL_{ins.offset - base:04x}: {nm} IL_{o - base:04x}")
        else:
            print(f"{ind}IL_{ins.offset - base:04x}: {nm} {o!r}")


def match(pat):
    rx = re.compile(pat, re.I)
    return [(rid, td_full(rid)) for rid in _TD_NAME if rx.search(td_full(rid))]


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "types"
    if cmd == "types":
        for rid, fn in sorted(match(sys.argv[2] if len(sys.argv) > 2 else "."), key=lambda x: x[1]):
            print(fn)
    elif cmd == "sig":
        for trid, fn in sorted(match(sys.argv[2] if len(sys.argv) > 2 else "."), key=lambda x: x[1]):
            r = TD[trid - 1]
            ext = r.Extends
            b = ""
            tn = getattr(getattr(ext, "table", None), "name", "")
            if tn == "TypeDef":
                b = td_full(ext.row_index)
            elif tn == "TypeRef":
                b = tr_full(ext.row_index)
            print(f"\n=== {fn}" + (f" : {b}" if b else ""))
            for frid in [k for k, v in FIELD_OWNER.items() if v == trid]:
                try:
                    t = f_sig(FR[frid - 1].Signature.value)
                except Exception:
                    t = "?"
                print(f"    field {t} {str(FR[frid-1].Name)}")
            for mrid in sorted([k for k, v in METHOD_OWNER.items() if v == trid]):
                nm = str(MR[mrid - 1].Name)
                if re.match(r"^(get_|set_|add_|remove_)", nm):
                    continue
                print("    " + mdef_str(mrid).split("::", 1)[-1])
    elif cmd == "il":
        pat, filt = sys.argv[2], (sys.argv[3] if len(sys.argv) > 3 else None)
        for trid, fn in sorted(match(f"^{re.escape(pat)}$"), key=lambda x: x[1]):
            print(f"\n===== {fn}")
            for mrid in sorted([k for k, v in METHOD_OWNER.items() if v == trid]):
                nm = str(MR[mrid - 1].Name)
                if filt and not re.search(filt, nm, re.I):
                    continue
                print(f"\n// {mdef_str(mrid)}")
                dump(mrid)
    elif cmd == "consts":
        pat = sys.argv[2] if len(sys.argv) > 2 else "."
        for trid, fn in sorted(match(pat), key=lambda x: x[1]):
            printed = False
            for frid in [k for k, v in FIELD_OWNER.items() if v == trid]:
                fr = FR[frid - 1]
                val = const_of(frid)
                if val is not None:
                    if not printed:
                        print(f"\n=== {fn}")
                        printed = True
                    try:
                        t = f_sig(fr.Signature.value)
                    except Exception:
                        t = "?"
                    print(f"    const {t} {str(fr.Name)} = {decode_const(val, t)!r}")
    elif cmd == "xr":
        keys = sys.argv[2:]
        pats = [re.compile(k, re.I) for k in keys]
        print("// scanning...", file=sys.stderr)
        for mrid, owner in sorted(METHOD_OWNER.items()):
            r = MR[mrid - 1]
            if r.Rva == 0:
                continue
            try:
                body = CilMethodBody(R(r))
            except Exception:
                continue
            hits = set()
            for ins in body.instructions:
                o = ins.operand
                if not isinstance(o, Token):
                    continue
                s2 = resolve(o)
                for pt in pats:
                    if pt.search(s2):
                        hits.add(s2)
            if hits:
                print(f"{mdef_str(mrid)}")
                for h in sorted(hits):
                    print(f"      -> {h}")
    elif cmd == "ustr":
        rx = re.compile(sys.argv[2], re.I)
        data = open(DLL, "rb").read()
        seen = set()
        for m in re.finditer(rb"(?:[\x20-\x7e]\x00){3,}", data):
            s = m.group().decode("utf-16-le")
            if rx.search(s) and s not in seen:
                seen.add(s)
                print(s)


if __name__ == "__main__":
    main()

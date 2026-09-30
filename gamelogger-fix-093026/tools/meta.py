import struct, sys
class Meta:
    def __init__(s, path):
        b = s.b = open(path,'rb').read()
        h = struct.unpack('<' + 'i'*64, b[8:264])
        names = ['stringLiteral','stringLiteralData','string','events','properties','methods','parameterDefaultValues','fieldDefaultValues','fieldAndParameterDefaultValueData','fieldMarshaledSizes','parameters','fields','genericParameters','genericParameterConstraints','genericContainers','nestedTypes','interfaces','vtableMethods','interfaceOffsets','typeDefinitions','images','assemblies']
        s.sec = {n: (h[2*i], h[2*i+1]) for i, n in enumerate(names)}
    def str(s, idx):
        off = s.sec['string'][0] + idx; e = s.b.index(b'\0', off); return s.b[off:e].decode('utf-8','replace')
    def types(s):
        off, size = s.sec['typeDefinitions']
        for i in range(size // 88):
            r = struct.unpack_from('<16i8H2I', s.b, off + 88*i)
            yield i, dict(name=s.str(r[0]), ns=s.str(r[1]), fieldStart=r[8], methodStart=r[9], nestedStart=r[12], mcount=r[16], pcount=r[17], fcount=r[18], ncount=r[20])
    def field(s, i):
        off = s.sec['fields'][0] + 12*i; n, t, tok = struct.unpack_from('<iiI', s.b, off); return s.str(n), t
    def method(s, i):
        off = s.sec['methods'][0] + 36*i; r = struct.unpack_from('<7i4H', s.b, off); return s.str(r[0]), r[10]
    def field_defaults(s):
        off, size = s.sec['fieldDefaultValues']; d = {}
        for i in range(size // 12):
            fi, ti, di = struct.unpack_from('<iii', s.b, off + 12*i); d[fi] = di
        return d
    def literals(s):
        off, size = s.sec['stringLiteral']; doff = s.sec['stringLiteralData'][0]; n = size // 8; out = []
        for i in range(n):
            ln, di = struct.unpack_from('<Ii', s.b, off + 8*i)
            out.append(s.b[doff+di:doff+di+ln].decode('utf-8','replace'))
        return out

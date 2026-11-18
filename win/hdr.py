for p in (r"C:\temp\alterum\music\sintel_pipe\out.mp3", r"C:\temp\alterum\music\bbb_pipe\out.mp3"):
    b = open(p, "rb").read(64)
    print(p)
    print("  hex  :", " ".join("%02x" % x for x in b[:16]))
    print("  ascii:", b[:10])
    if b[:3] == b"ID3":
        size = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9]
        print("  ID3v2.%d flags=0x%02x size=%d" % (b[3], b[5], size))
    s = open(p, "rb").read(400000)
    for tag in (b"Xing", b"Info", b"LAME"):
        print("  %s at %d" % (tag.decode(), s.find(tag)))

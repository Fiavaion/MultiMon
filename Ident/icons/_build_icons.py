import struct, os
import numpy as np
from PIL import Image, ImageFilter, ImageDraw, ImageChops
OUT=r"D:\AIprojects\MultiMon\Ident\release"
os.makedirs(OUT,exist_ok=True)

def key_background(im, bg_is_dark, tol):
    """Make the flat background transparent via flood-fill from the corners; keeps the wall's own dark/white interior."""
    rgb=np.array(im.convert("RGB")).astype(int)
    ref=np.array([0,0,0]) if bg_is_dark else np.array([255,255,255])
    near=(np.abs(rgb-ref).max(axis=2)<=tol)
    h,w=near.shape; seen=np.zeros_like(near); stack=[(0,0),(0,w-1),(h-1,0),(h-1,w-1)]
    from collections import deque; q=deque(stack)
    while q:
        y,x=q.popleft()
        if y<0 or x<0 or y>=h or x>=w or seen[y,x] or not near[y,x]: continue
        seen[y,x]=True; q.extend([(y+1,x),(y-1,x),(y,x+1),(y,x-1)])
    alpha=(~seen).astype(np.uint8)*255
    a=Image.fromarray(alpha).filter(ImageFilter.GaussianBlur(0.8))
    out=im.convert("RGBA"); out.putalpha(a); return out

def crop_to_alpha(im, pad=0):
    bb=im.getchannel("A").point(lambda v:255 if v>8 else 0).getbbox()
    return im.crop((bb[0]-pad,bb[1]-pad,bb[2]+pad,bb[3]+pad))

def squircle_mask(size, n=5.0, ss=4):
    S=size*ss; y,x=np.mgrid[0:S,0:S]; cx=cy=(S-1)/2; r=S/2
    v=(np.abs((x-cx)/r)**n+np.abs((y-cy)/r)**n)<=1
    return Image.fromarray((v*255).astype(np.uint8)).resize((size,size),Image.LANCZOS)

def resize(im,s):
    r=im.resize((s,s),Image.LANCZOS)
    if s<=64: r=r.filter(ImageFilter.UnsharpMask(radius=0.8,percent=60,threshold=2))
    return r

def write_ico(images, path):
    # PNG-compressed entries (Vista+), sizes <=256
    entries=[]; data=b""; off=6+16*len(images)
    for im in images:
        import io; b=io.BytesIO(); im.save(b,"PNG"); png=b.getvalue()
        s=im.width; entries.append(struct.pack("<BBBBHHII",s if s<256 else 0,s if s<256 else 0,0,0,1,32,len(png),off+len(data))); data+=png
    open(path,"wb").write(struct.pack("<HHH",0,1,len(images))+b"".join(entries)+data)

def write_icns(im1024, path):
    types=[("icp4",16),("icp5",32),("icp6",64),("ic07",128),("ic08",256),("ic09",512),("ic10",1024),
           ("ic11",32),("ic12",64),("ic13",256),("ic14",512)]
    body=b""
    for t,s in types:
        import io; b=io.BytesIO(); resize(im1024,s).save(b,"PNG"); png=b.getvalue()
        body+=t.encode()+struct.pack(">I",8+len(png))+png
    open(path,"wb").write(b"icns"+struct.pack(">I",8+len(body))+body)

# ---------- PC (Windows) ----------
pc=Image.open("Multimon_PC.png").convert("RGB")
pc_t=key_background(pc,True,28)
pc_wall=crop_to_alpha(pc_t)
# 1024 canvas, wall fills ~94% (Windows icons use near-full canvas)
def on_canvas(wall, size=1024, fill=0.94):
    w,h=wall.size; s=fill*size/max(w,h); wall=wall.resize((int(w*s),int(h*s)),Image.LANCZOS)
    c=Image.new("RGBA",(size,size),(0,0,0,0)); c.paste(wall,((size-wall.width)//2,(size-wall.height)//2),wall); return c
pc_icon=on_canvas(pc_wall)
pc_icon.save(f"{OUT}/multimon-win-1024.png")
for s in [16,24,32,48,64,128,256,512]: resize(pc_icon,s).save(f"{OUT}/multimon-win-{s}.png")
write_ico([resize(pc_icon,s) for s in [16,24,32,48,64,128,256]], f"{OUT}/MultiMon.ico")
pc.save(f"{OUT}/multimon-pc-ident-1024.png")

# ---------- macOS ----------
mac=Image.open("Mac_MultiMon.psd").convert("RGB")
mac.save(f"{OUT}/multimon-mac-ident-1024.png")
mac_t=key_background(mac,False,14)
mac_wall=crop_to_alpha(mac_t)
# Apple HIG: 1024 canvas, squircle ~824px, content inside with margin; soft shadow under the squircle
canvas=Image.new("RGBA",(1024,1024),(0,0,0,0))
sq=squircle_mask(824)
plate=Image.new("RGBA",(824,824),(246,247,249,255))
# subtle vertical gradient on the squircle
g=np.linspace(255,236,824).astype(np.uint8); grad=np.stack([np.tile(g[:,None],(1,824))]*3+[np.full((824,824),255,np.uint8)],-1)
plate=Image.fromarray(grad,"RGBA"); plate.putalpha(sq)
shadow=Image.new("RGBA",(1024,1024),(0,0,0,0)); sh=Image.new("RGBA",(824,824),(0,0,0,110)); sh.putalpha(ImageChops.multiply(sq,Image.new("L",(824,824),110)))
shadow.paste(sh,(100,112),sh); shadow=shadow.filter(ImageFilter.GaussianBlur(14))
canvas.alpha_composite(shadow); canvas.alpha_composite(plate,(100,100))
w,h=mac_wall.size; s=700/max(w,h); ww=mac_wall.resize((int(w*s),int(h*s)),Image.LANCZOS)
canvas.alpha_composite(ww,((1024-ww.width)//2,(1024-ww.height)//2))
canvas.save(f"{OUT}/multimon-macos-1024.png")
iconset=f"{OUT}/MultiMon.iconset"; os.makedirs(iconset,exist_ok=True)
for s in [16,32,128,256,512]:
    resize(canvas,s).save(f"{iconset}/icon_{s}x{s}.png"); resize(canvas,s*2).save(f"{iconset}/icon_{s}x{s}@2x.png")
write_icns(canvas, f"{OUT}/MultiMon.icns")

# ---------- web / shared ----------
for name,img in [("multimon-pc",pc),("multimon-mac",mac)]:
    for s in [512,256]: img.resize((s,s),Image.LANCZOS).save(f"{OUT}/{name}-{s}.png")
for s in [32,16]: resize(pc_icon,s).save(f"{OUT}/favicon-{s}.png")
write_ico([resize(pc_icon,s) for s in [16,32,48]], f"{OUT}/favicon.ico")
# preview sheet
sheet=Image.new("RGBA",(1024,560),(40,40,40,255)); x=8
for s in [256,128,64,48,32,16]:
    sheet.alpha_composite(resize(pc_icon,s),(x,8)); sheet.alpha_composite(resize(canvas,s),(x,300)); x+=s+16
sheet.convert("RGB").save(f"{OUT}/_icon-preview.png")
print("done"); print("\n".join(sorted(os.listdir(OUT))))

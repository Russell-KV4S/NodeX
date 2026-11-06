"""Regenerates every NodeX brand raster from branding/source/*.svg.  Run: python build_assets.py"""
import os, io, pymupdf as fitz
from PIL import Image, ImageDraw, ImageFont
HERE=os.path.dirname(os.path.abspath(__file__))
SRC=os.path.join(HERE,"source"); OUT=os.path.join(HERE,"exports")
REPO=os.path.dirname(HERE)
UI=os.path.join(REPO,"src","NodeX.UI")
ICON_BG=(232,238,252,255)    # #E8EEFC pale-blue tile for app icons (navy-outline mark)
BG=(13,17,23,255)              # #0D1117, same as the app's ConsoleBackgroundBrush
os.makedirs(OUT,exist_ok=True)

def mark(name,px):
    d=fitz.open(os.path.join(SRC,f"nodex-mark-{name}.svg"))
    p=fitz.open("pdf",d.convert_to_pdf())[0]
    pix=p.get_pixmap(matrix=fitz.Matrix(px/480,px/480),alpha=True)
    return Image.open(io.BytesIO(pix.tobytes("png"))).convert("RGBA")

def on_canvas(name,canvas,frac,bg=None):
    im=Image.new("RGBA",(canvas,canvas),bg or (0,0,0,0))
    m=mark(name,round(canvas*frac)); o=(canvas-m.width)//2
    im.alpha_composite(m,(o,o)); return im

def rounded(im,radius_frac):
    s=im.width; mask=Image.new("L",(s*4,s*4),0)
    ImageDraw.Draw(mask).rounded_rectangle((0,0,s*4-1,s*4-1),radius=int(s*4*radius_frac),fill=255)
    mask=mask.resize((s,s),Image.LANCZOS); out=im.copy(); out.putalpha(mask); return out

def save(im,path):
    os.makedirs(os.path.dirname(path),exist_ok=True); im.save(path)

FONT_B="C:/Windows/Fonts/segoeuib.ttf"; FONT_R="C:/Windows/Fonts/segoeui.ttf"
def font(p,s): return ImageFont.truetype(p,s)

# ---- masters / exports ----
save(mark("dark",1024),f"{OUT}/nodex-mark-dark.png")
save(mark("light",1024),f"{OUT}/nodex-mark-light.png")
icon1024=on_canvas("light",1024,0.80,ICON_BG); save(icon1024,f"{OUT}/nodex-app-icon-1024.png")        # iOS/store master (square, no alpha needed)
save(icon1024.convert("RGB").resize((512,512),Image.LANCZOS),f"{OUT}/nodex-play-store-icon-512.png")

def lockup(variant,h=400):
    dark=variant=="dark"; fg=(240,246,252,255) if dark else (16,18,110,255); sub=(139,148,158,255) if dark else (90,96,110,255)
    m=mark(variant,h); f1=font(FONT_B,int(h*0.40)); f2=font(FONT_R,int(h*0.13))
    w=m.width+int(h*0.05)+int(max(f1.getlength("NodeX"),f2.getlength("AllStarLink Radioless Node")))+int(h*0.06)
    im=Image.new("RGBA",(w,h),(0,0,0,0)); im.alpha_composite(m,(0,0)); d=ImageDraw.Draw(im); x=m.width+int(h*0.05)
    d.text((x,int(h*0.26)),"NodeX",font=f1,fill=fg); d.text((x+4,int(h*0.26)+int(h*0.52)),"AllStarLink Radioless Node",font=f2,fill=sub); return im
save(lockup("dark"),f"{OUT}/nodex-lockup-dark.png"); save(lockup("light"),f"{OUT}/nodex-lockup-light.png")

# Play feature graphic 1024x500
fg=Image.new("RGBA",(1024,500),BG); d=ImageDraw.Draw(fg)
for i in range(500):   # subtle vertical lift toward the bottom
    c=int(13+ (i/500)*10); d.line((0,i,1024,i),fill=(c,c+4,c+10,255))
m=mark("dark",400); fg.alpha_composite(m,(70,50))
d.text((500,150),"NodeX",font=font(FONT_B,128),fill=(240,246,252,255))
d.text((506,300),"AllStarLink Radioless Node",font=font(FONT_R,38),fill=(245,158,11,255))
save(fg.convert("RGB"),f"{OUT}/nodex-play-feature-graphic-1024x500.png")

# ---- Android ----
A=f"{UI}/NodeX.UI.Android/Resources"
for dens,px in (("mdpi",108),("hdpi",162),("xhdpi",216),("xxhdpi",324),("xxxhdpi",432)):
    save(on_canvas("light",px,0.62),f"{A}/mipmap-{dens}/ic_launcher_foreground.png")
    save(on_canvas("mono",px,0.62),f"{A}/mipmap-{dens}/ic_launcher_monochrome.png")
for dens,px in (("mdpi",24),("hdpi",36),("xhdpi",48),("xxhdpi",72),("xxxhdpi",96)):
    save(on_canvas("mono",px,1.0),f"{A}/drawable-{dens}/ic_stat_nodex.png")
save(on_canvas("dark",432,0.62),f"{A}/drawable-nodpi/splash_logo.png")

# ---- Avalonia (shared UI, desktop window icon, browser) ----
save(mark("dark",256),f"{UI}/NodeX.UI/Assets/nodex-mark.png")
save(icon1024.resize((512,512),Image.LANCZOS),f"{UI}/NodeX.UI/Assets/Icon.png")
ico=rounded(on_canvas("light",1024,0.80,ICON_BG),0.22)
ico.save(f"{UI}/NodeX.UI/Assets/nodex.ico",sizes=[(s,s) for s in (16,24,32,48,64,128,256)])
ico.save(f"{UI}/NodeX.UI.Browser/wwwroot/favicon.ico",sizes=[(s,s) for s in (16,32,48)])
save(ico.resize((192,192),Image.LANCZOS),f"{UI}/NodeX.UI.Browser/wwwroot/icon-192.png")
print("done")

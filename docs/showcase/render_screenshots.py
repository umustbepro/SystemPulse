from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

W,H=1380,900
BG="#0b0e13"; SURFACE="#12161e"; RAISED="#181d27"; BORDER="#29303c"; TEXT="#f2f5fa"; MUTED="#9ca6b8"; ACCENT="#58d6c7"; BLUE="#6ea8fe"; PURPLE="#b798ff"; WARN="#ffb454"
OUT=Path(__file__).resolve().parents[1]/"screenshots"; OUT.mkdir(parents=True,exist_ok=True)
REG=r"C:\Windows\Fonts\segoeui.ttf"; SEMI=r"C:\Windows\Fonts\seguisb.ttf"; MONO=r"C:\Windows\Fonts\consola.ttf"
def font(n,b=False,m=False): return ImageFont.truetype(MONO if m else SEMI if b else REG,n)
def txt(d,xy,s,n=13,c=TEXT,b=False,m=False,anchor=None): d.text(xy,s,font=font(n,b,m),fill=c,anchor=anchor)
def rr(d,box,r=10,fill=SURFACE,outline=BORDER,w=1): d.rounded_rectangle(box,r,fill=fill,outline=outline,width=w)
def line(d,pts,c=ACCENT,w=2): d.line(pts,fill=c,width=w,joint="curve")
NAV=[("⌂","Overview","overview"),("ϟ","Performance","performance"),("↗","Overclock (BETA)","overclock"),("▥","Processes","processes"),("⌁","Network","network"),("▭","Storage","storage"),("⌁","Sensor details","sensors"),("♲","Storage Cleanup","cleanup"),("◷","History & alerts","history")]
def shell(page,title,desc,right="Updated 19:24:56\nEvery 2 seconds"):
 im=Image.new("RGB",(W,H),BG);d=ImageDraw.Draw(im)
 d.rectangle((0,0,W,52),fill=SURFACE);d.line((0,51,W,51),fill=BORDER);d.rectangle((0,52,218,H),fill=SURFACE);d.line((217,52,217,H),fill=BORDER)
 rr(d,(18,13,44,39),6,ACCENT,ACCENT);txt(d,(31,26),"∿",17,"#071311",True,anchor="mm");txt(d,(54,26),"SystemPulse",15,TEXT,True,anchor="lm");txt(d,(151,26),"v08.1",12,MUTED,anchor="lm");txt(d,(238,26),"Hardware monitor",12,MUTED,anchor="lm")
 for x,s in [(1194,"⟳"),(1240,"−"),(1286,"□"),(1332,"×")]: txt(d,(x,26),s,17,MUTED,anchor="mm")
 txt(d,(26,72),"MONITOR",10,MUTED,True)
 y=88
 for icon,label,key in NAV:
  if key==page: rr(d,(14,y,204,y+43),7,"#183034","#183034")
  txt(d,(29,y+21),icon,16,ACCENT if key==page else MUTED,anchor="mm");txt(d,(52,y+21),label,13,TEXT,anchor="lm");y+=46
 rr(d,(14,758,204,825),8,"#112225","#27504e");txt(d,(29,774),"●  LIVE MONITORING",10,ACCENT,True);txt(d,(29,802),"System is nominal",11,ACCENT);txt(d,(109,844),"Created by youmustbepro ♡",11,MUTED,anchor="mm");txt(d,(109,866),"Changelog",11,MUTED,anchor="mm");d.line((80,872,138,872),fill=MUTED)
 txt(d,(248,76),title,28,TEXT,True);txt(d,(248,110),desc,13,MUTED)
 for i,s in enumerate(right.split("\n")): txt(d,(1348,80+i*15),s,11,MUTED,anchor="ra")
 return im,d
def badge(d,x,y,s,c=ACCENT): rr(d,(x,y,x+42,y+42),9,"#173130","#173130");txt(d,(x+21,y+21),s,10,c,True,anchor="mm")
def pill(d,x,y): rr(d,(x,y,x+72,y+24),12,"#17232a","#17232a");txt(d,(x+36,y+12),"● Normal",10,ACCENT,anchor="mm")
def spark(d,box,c=ACCENT):
 x1,y1,x2,y2=box; vals=[.58,.42,.63,.35,.52,.28,.66,.44,.53,.3,.48,.34,.46];pts=[(x1+i*(x2-x1)/(len(vals)-1),y1+v*(y2-y1)) for i,v in enumerate(vals)];line(d,pts,c,2);d.line((x1,y2,x2,y2),fill="#202733")
def facts(d,x,y,items):
 for i,(a,b) in enumerate(items): xx=x+(i%2)*94;yy=y+(i//2)*58;txt(d,(xx,yy),a.upper(),10,MUTED);txt(d,(xx,yy+17),b,14,TEXT)
def hwcard(d,y,tag,name,sub,temp,items,c=ACCENT):
 rr(d,(248,y,1348,y+190));badge(d,270,y+45,tag,c);txt(d,(325,y+51),name,14,TEXT,True);txt(d,(325,y+73),sub,11,MUTED);pill(d,270,y+103);txt(d,(515,y+82),temp,43,TEXT,True);txt(d,(570,y+74),"° C",15,MUTED);txt(d,(515,y+128),"Package temperature",11,MUTED);spark(d,(700,y+70,1030,y+125),c);facts(d,1090,y+49,items)
def overview():
 im,d=shell("overview","System overview","A live view of temperatures and component activity.","Updated 19:24:56\nFree RAM    ⟳    ☼");hwcard(d,132,"CPU","AMD Ryzen 9 9950X3D","16-Core Processor","54",[("Voltage","1.27 V"),("Power","142 W"),("Load","18%"),("Source","PawnIO")]);hwcard(d,336,"GPU","NVIDIA GeForce RTX 5090","Core temperature","61",[("Voltage","0.975 V"),("Board power","412 W"),("Load","37%"),("Source","NVIDIA driver")],BLUE)
 for x,tag,name,t,c in [(248,"SSD","Samsung SSD 9100 PRO 4TB","38",PURPLE),(806,"MB","ASUS ROG CROSSHAIR X870E HERO","35",ACCENT)]: rr(d,(x,540,x+542,818));badge(d,x+22,564,tag,c);txt(d,(x+77,575),name,14,TEXT,True);txt(d,(x+22,650),t,43,TEXT,True);txt(d,(x+76,643),"° C",15,MUTED);d.line((x+22,723,x+520,723),fill=c,width=3);txt(d,(x+22,750),"SOURCE",10,MUTED);txt(d,(x+22,770),"Hardware sensor · Healthy",11,MUTED)
 return im
def performance():
 im,d=shell("performance","Performance","Live component activity, frame pacing, and per-drive throughput.");hwcard(d,132,"CPU","PROCESSOR LOAD","AMD Ryzen 9 9950X3D","21",[("Load","21%"),("Power","142 W"),("Clock","5.7 GHz"),("Memory","41%")]);hwcard(d,336,"GPU","GRAPHICS ACTIVITY","NVIDIA GeForce RTX 5090","61",[("GPU load","74%"),("Frame rate","238 FPS"),("Frame time","4.2 ms"),("Application","Cyberpunk 2077")],BLUE);hwcard(d,540,"SSD","STORAGE ACTIVITY","Samsung SSD 9100 PRO 4TB","38",[("Active time","12%"),("Read","2.4 GB/s"),("Write","684 MB/s"),("Queue","0.4")],PURPLE);return im
def sliders(d,x,y,gpu):
 names=["Core clock","Memory clock","Core voltage","Power limit"];vals=["3120 MHz","1750 MHz","1080 mV","575 W"] if gpu else ["5700 MHz","Firmware controlled","Firmware controlled","181 W"]
 for i,(n,v) in enumerate(zip(names,vals)): yy=y+i*75;txt(d,(x,yy),n,13,TEXT,True);txt(d,(x+480,yy),v,12,MUTED,anchor="ra");d.line((x,yy+29,x+480,yy+29),fill=RAISED,width=4);d.line((x,yy+29,x+270+(i%2)*30,yy+29),fill=ACCENT,width=4);d.ellipse((x+264+(i%2)*30,yy+23,x+276+(i%2)*30,yy+35),fill=TEXT,outline=ACCENT,width=3)
def overclock():
 im,d=shell("overclock","Overclock  BETA","Capability-checked CPU and GPU performance tuning with vendor-backed controls.","Tuning support detected");rr(d,(248,132,1348,191),9,"#211b14","#5a4528");txt(d,(270,154),"!  Performance tuning can cause instability, extra heat, or component damage",13,WARN,True);txt(d,(270,175),"Apply one small change at a time and monitor temperatures.",11,MUTED)
 for x,tag,name,back,gpu in [(248,"CPU","Intel(R) Core(TM) i9-14900KS","PawnIO direct Intel tuning · package power writable",False),(806,"GPU","NVIDIA GeForce RTX 5090","NVIDIA driver control · values applied through nvidia-smi",True)]: rr(d,(x,207,x+542,818));badge(d,x+22,229,tag,BLUE if gpu else ACCENT);txt(d,(x+77,235),"GRAPHICS PROFILE" if gpu else "PROCESSOR PROFILE",14,TEXT,True);txt(d,(x+77,257),name,11,MUTED);rr(d,(x+22,287,x+520,334),7,RAISED,RAISED);txt(d,(x+34,310),back,11,MUTED,anchor="lm");sliders(d,x+22,365,gpu);rr(d,(x+22,696,x+194,735),7,ACCENT,ACCENT);txt(d,(x+108,715),"Apply supported controls",11,"#071311",True,anchor="mm");rr(d,(x+205,696,x+324,735),7,RAISED,BORDER);txt(d,(x+264,715),"Restore defaults",11,TEXT,anchor="mm")
 return im
def processes():
 im,d=shell("processes","Processes","Live CPU, memory, and disk activity by application.","186 processes");rr(d,(248,132,1220,170),7,SURFACE,BORDER);txt(d,(263,151),"⌕  Filter processes",12,MUTED,anchor="lm");rr(d,(1232,132,1348,170),7,RAISED,BORDER);txt(d,(1290,151),"Refresh now",11,TEXT,anchor="mm");rr(d,(248,184,1348,815));cols=[248,725,860,970,1110,1348];heads=["PROCESS NAME ↕","PID ↕","CPU ↕","MEMORY ↕","DISK ↕"]
 for i,h in enumerate(heads):txt(d,(cols[i]+18,207),h,10,MUTED,True)
 rows=[("Cyberpunk 2077","18420","38.7%","12.4 GB","486 MB/s"),("Blender","22084","21.3%","8.8 GB","142 MB/s"),("SystemPulse","30116","2.1%","186 MB","4.2 MB/s"),("NVIDIA Container","4472","1.4%","218 MB","1.1 MB/s"),("Discord","11280","0.8%","684 MB","624 KB/s"),("Steam Client WebHelper","13544","0.4%","392 MB","184 KB/s"),("Windows Explorer","5296","0.2%","244 MB","48 KB/s"),("Desktop Window Manager","1940","0.1%","312 MB","0 B/s"),("System","4","0.1%","12 MB","2.8 MB/s"),("SearchHost","8240","0.0%","148 MB","0 B/s")]
 for r,row in enumerate(rows): y=228+r*56;d.line((248,y,1348,y),fill="#202632");
 for i,v in enumerate(row):txt(d,(cols[i]+18,y+28),v,12,TEXT,b=i==0,anchor="lm")
 return im
def network():
 im,d=shell("network","Network","Live throughput and connection details for every adapter.");data=[("10G Ethernet","Marvell AQtion 10Gbit Network Adapter","4.8 Gbps","1.2 Gbps"),("Wi-Fi 7","Qualcomm FastConnect 7800","812 Mbps","164 Mbps"),("Bluetooth Network","Personal Area Network","0 Kbps","0 Kbps")]
 for i,r in enumerate(data):y=132+i*170;rr(d,(248,y,1348,y+155));badge(d,270,y+45,"NET");txt(d,(325,y+51),r[0],14,TEXT,True);txt(d,(325,y+73),r[1],11,MUTED);txt(d,(325,y+96),f"192.168.1.{40+i}",11,MUTED);spark(d,(590,y+48,970,y+110),BLUE if i else ACCENT);facts(d,1030,y+35,[("Download",r[2]),("Upload",r[3]),("Link speed","10 Gbps"), ("Status","Connected")])
 return im
def storage():
 im,d=shell("storage","Storage","Health, identity, capacity, and live activity for every physical drive.");data=[("Samsung SSD 9100 PRO 4TB","NVMe · PCIe 5.0 x4","3.64 TB","38 °C"),("WD_BLACK SN850X 4TB","NVMe · PCIe 4.0 x4","3.64 TB","42 °C"),("Seagate Exos X24 24TB","HDD · SATA 6 Gb/s","21.8 TB","34 °C")]
 for i,r in enumerate(data):y=132+i*190;rr(d,(248,y,1348,y+175));badge(d,270,y+28,"SSD",PURPLE);txt(d,(333,y+37),r[0],14,TEXT,True);txt(d,(333,y+60),r[1],11,MUTED);d.line((333,y+103,840,y+103),fill=PURPLE,width=5);txt(d,(333,y+126),r[2]+" capacity · Healthy",11,MUTED);facts(d,950,y+26,[("Temperature",r[3]),("Health","Healthy"),("Read","2.4 GB/s"),("Write","684 MB/s")])
 return im
def sensors():
 im,d=shell("sensors","Sensor details","Raw readings and their detected hardware sources.");data=[("CPU package","54 °C","PawnIO · package sensor"),("CPU voltage","1.27 V","PawnIO · IA32_PERF_STATUS"),("CPU package power","142 W","PawnIO · energy counter"),("GPU core","61 °C","NVIDIA display driver"),("GPU voltage","0.975 V","NVAPI voltage telemetry"),("GPU board power","412 W","NVIDIA whole-board power"),("Motherboard","35 °C","LibreHardwareMonitor · EC"),("Storage","38 °C","NVMe SMART health log"),("Physical memory","41%","Windows native metrics"),("Frame time","4.2 ms","PresentMon ETW capture"),("Network receive","4.8 Gbps","Windows adapter counters"),("CPU fan","1,640 RPM","Super I/O controller")]
 for i,r in enumerate(data):x=248+(i%3)*372;y=132+(i//3)*157;rr(d,(x,y,x+356,y+142));txt(d,(x+20,y+20),r[0],13,TEXT,True);txt(d,(x+20,y+55),r[1],27,ACCENT,True);txt(d,(x+20,y+99),"SOURCE",10,MUTED);txt(d,(x+20,y+117),r[2],11,MUTED)
 return im
def cleanup():
 im,d=shell("cleanup","Storage Cleanup  BETA","Find temporary files and review large files inactive for six months or longer.","Scan complete for D: · FAST STORAGE");rr(d,(248,132,1348,190),9,"#211b14","#5a4528");txt(d,(270,154),"Folder-based cleanup safety",13,WARN,True);txt(d,(270,176),"Keep or Delete applies to every detected large file in the parent folder.",11,MUTED);rr(d,(248,205,824,818));rr(d,(838,205,1348,818));txt(d,(270,226),"SCAN OUTPUT",10,MUTED,True);txt(d,(270,251),"18.6 GB eligible temporary files found",14,TEXT,True);rr(d,(270,280,802,710),6,RAISED,BORDER);logs=["19:24:03  Started a read-only scan of D:\\","19:24:04  Checking temporary files and caches","19:24:06  Found 2.4 GB in Windows temporary storage","19:24:08  Found 8.7 GB in application caches","19:24:10  Reviewing large files inactive for six months","19:24:13  Review required: D:\\Games\\Archive\\Benchmark.exe","19:24:17  Review required: D:\\Video\\Exports\\Demo-Reel.mov","19:24:20  Scan complete · 2 folders require review"]
 for i,s in enumerate(logs):txt(d,(284,298+i*23),s,10,"#a8b1c1",m=True)
 rr(d,(270,730,453,770),7,RAISED,BORDER);txt(d,(361,750),"Delete eligible temporary files",10,TEXT,anchor="mm");txt(d,(860,226),"FILE REVIEW",10,MUTED,True);txt(d,(860,247),"Non-temporary files are never deleted automatically.",11,MUTED)
 for i,(p,s) in enumerate([("D:\\Games\\Archive\\Benchmark.exe","9.8 GB · Last activity 2025-08-14"),("D:\\Video\\Exports\\Demo-Reel.mov","6.2 GB · Last activity 2025-06-02")]):y=280+i*170;rr(d,(860,y,1326,y+148),7,RAISED,BORDER);txt(d,(876,y+20),p,12,TEXT,True);txt(d,(876,y+43),s,11,MUTED);txt(d,(876,y+65),"Approval deletes the dedicated parent folder.",10,MUTED);x=876
 for label,w,c in [("Open folder",88,RAISED),("Keep folder",88,RAISED),("Delete folder",96,ACCENT)]:rr(d,(x,y+94,x+w,y+130),7,c,BORDER if c==RAISED else ACCENT);txt(d,(x+w/2,y+112),label,10,"#071311" if c==ACCENT else TEXT,anchor="mm");x+=w+8
 return im
def history():
 im,d=shell("history","History & alerts","Review recent telemetry, export reports, and configure notifications.");
 for y,title,val,c in [(132,"CPU temperature history","54 °C",ACCENT),(389,"GPU temperature history","61 °C",BLUE)]:rr(d,(248,y,935,y+241));txt(d,(270,y+22),title,13,TEXT,True);txt(d,(270,y+43),"Last 30 minutes",11,MUTED);txt(d,(900,y+29),val,25,c,True,anchor="ra");spark(d,(270,y+85,912,y+205),c)
 rr(d,(951,132,1348,630));txt(d,(973,157),"Alert settings",15,TEXT,True);settings=[("CPU temperature warning","85 °C"),("GPU temperature warning","85 °C"),("Storage temperature warning","65 °C"),("Storage health warning","Enabled"),("Notification-area alerts","Enabled")]
 for i,(a,b) in enumerate(settings):y=190+i*68;txt(d,(973,y),a,12,TEXT,True);txt(d,(973,y+20),b,11,MUTED);rr(d,(1285,y,1321,y+20),10,ACCENT,ACCENT);d.ellipse((1303,y+3,1317,y+17),fill=TEXT);d.line((973,y+50,1326,y+50),fill=BORDER)
 rr(d,(973,548,1075,587),7,ACCENT,ACCENT);txt(d,(1024,567),"Save settings",10,"#071311",True,anchor="mm");rr(d,(1087,548,1175,587),7,RAISED,BORDER);txt(d,(1131,567),"Export CSV",10,TEXT,anchor="mm");return im
for name,fn in [("overview",overview),("performance",performance),("overclock",overclock),("processes",processes),("network",network),("storage",storage),("sensor-details",sensors),("storage-cleanup",cleanup),("history-alerts",history)]: fn().save(OUT/f"{name}.png",optimize=True)
print(f"Rendered 9 screenshots to {OUT}")

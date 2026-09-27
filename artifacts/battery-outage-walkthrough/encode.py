"""Encode real CDP frames into a captioned tutorial; trim only static capture gaps."""
import json, subprocess
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
ROOT=Path(__file__).resolve().parent
FONT='/System/Library/Fonts/Supplemental/Arial.ttf'
data=json.loads((ROOT/'capture.json').read_text())
# Discard the first setup take, recorded before the idle-heartbeat fix.
data['scenes']=data['scenes'][2:]
segments=[]
for n,scene in enumerate(data['scenes']):
 frames=[f for f in data['frames'] if scene['start'] <= f['time'] <= scene['end']]
 if not frames: raise RuntimeError('Empty scene '+scene['title'])
 concat=ROOT/f'scene-{n:02}.txt'
 rendered=ROOT/f'rendered-{n:02}'
 rendered.mkdir(exist_ok=True)
 lines=[]
 for i,frame in enumerate(frames):
  next_time=frames[i+1]['time'] if i+1<len(frames) else scene['end']
  duration=max(.04,min(2.0,(next_time-frame['time'])/1000))
  canvas=Image.new('RGB',(1280,832),'#101a2b')
  canvas.paste(Image.open(ROOT/'frames'/frame['file']),(0,112))
  draw=ImageDraw.Draw(canvas)
  draw.multiline_text((32,12),scene['title'],font=ImageFont.truetype(FONT,26),fill='white',spacing=6)
  draw.text((32,88),'HOME ASSISTANT  /  BATTERY OUTAGE WALKTHROUGH',font=ImageFont.truetype(FONT,12),fill='#99b9d4')
  output=rendered/frame['file']
  canvas.save(output,quality=92)
  lines += ["file '"+str(output)+"'",f'duration {duration:.6f}']
 lines += ["file '"+str(rendered/frames[-1]['file'])+"'"]
 concat.write_text('\n'.join(lines)+'\n')
 caption=ROOT/f'caption-{n:02}.txt'
 caption.write_text(scene['title'])
 video=ROOT/f'scene-{n:02}.mp4'
 vf='fps=15,format=yuv420p'
 subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-safe','0','-f','concat','-i',str(concat),'-vf',vf,'-c:v','libx264','-preset','fast','-crf','20',str(video)],check=True)
 segments.append(video)
(ROOT/'segments.txt').write_text(''.join("file '"+str(p)+"'\n" for p in segments))
mp4=ROOT/'battery-outage-walkthrough.mp4'
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-safe','0','-f','concat','-i',str(ROOT/'segments.txt'),'-c','copy','-movflags','+faststart',str(mp4)],check=True)
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-i',str(mp4),'-filter_complex','[0:v]fps=12,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle','-loop','0',str(ROOT/'battery-outage-walkthrough.gif')],check=True)
print(subprocess.check_output(['ffprobe','-v','error','-show_entries','format=duration,size','-show_entries','stream=width,height,nb_frames','-of','json',str(ROOT/'battery-outage-walkthrough.gif')],text=True))

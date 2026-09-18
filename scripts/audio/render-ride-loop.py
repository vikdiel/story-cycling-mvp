"""Original 'Camps Bay Ride' score v2: driving, faster, energetic. No samples.
64-second seamless Am/F/C/G progression with kick, driving 16th bass and fast arpeggio.
"""
import math, wave
from array import array
from pathlib import Path
RATE=22050
SECONDS=64
N=RATE*SECONDS
left=array('f',[0])*N
right=array('f',[0])*N
chords=[[45,52,57,60],[41,48,53,57],[43,50,55,59],[47,54,59,62]]  # Am F C G
def add(start,length,midi,gain,pan,kind='pluck'):
    freq=440*2**((midi-69)/12)
    count=int(length*RATE)
    for i in range(count):
        t=i/RATE
        if kind=='pad':
            env=min(t/1.2,1)*min((length-t)/2.5,1)
            s=math.sin(2*math.pi*freq*t)+.18*math.sin(2*math.pi*freq*2.001*t)
        elif kind=='bass':
            env=min(t/.015,1)*math.exp(-t/1.6)*min((length-t)/.12,1)
            s=math.sin(2*math.pi*freq*t)+.35*math.sin(2*math.pi*freq*.5*t)
        elif kind=='kick':
            env=math.exp(-t/0.12)
            s=math.sin(2*math.pi*(58-160*t)*t)
        else:
            env=min(t/.006,1)*math.exp(-t/.9)*min((length-t)/.12,1)
            s=math.sin(2*math.pi*freq*t)+.15*math.sin(2*math.pi*freq*2*t)*math.exp(-t/.6)
        v=s*env*gain
        k=(int(start*RATE)+i)%N
        left[k]+=v*math.sqrt(1-pan); right[k]+=v*math.sqrt(pan)
bar=4.0  # faster 4-second bars
for c in range(16):
    chord=chords[c%4]
    root=chord[0]
    for midi in chord: add(c*bar, bar+2, midi, .013, .3, 'pad')
    for b in range(4):
        add(c*bar+b*1.0, .3, 36, .32, .5, 'kick')
    for e in range(16):
        add(c*bar+e*.25, .25, root-12, .045, .5, 'bass')
        if e%4==2: add(c*bar+e*.25, .25, root, .03, .5, 'bass')
    arp=[chord[1],chord[2],chord[3],chord[2]+12,chord[3],chord[2],chord[1],chord[0]+12]
    for e in range(8):
        add(c*bar+e*.5, .7, arp[e]+12, .05, .3+(e%2)*.4, 'pluck')
peak=max(max(abs(v) for v in left),max(abs(v) for v in right))
scale=.68/max(peak,.001)
out=Path('Assets/StoryCycling/Resources/CampsBayRide.wav')
with wave.open(str(out),'wb') as w:
    w.setparams((2,2,RATE,0,'NONE','not compressed'))
    samples=array('h')
    for i in range(N): samples.extend((int(left[i]*scale*32767),int(right[i]*scale*32767)))
    w.writeframes(samples.tobytes())
print(f'Ride loop v2: {SECONDS}s, {out}')

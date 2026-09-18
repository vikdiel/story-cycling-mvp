"""Original 'Camps Bay Ride' score: energetic driving ride music, no samples.
64-second seamless Am/F/C/G progression, stereo PCM. Deterministic, regeneratable.
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
            env=min(t/1.5,1)*min((length-t)/3,1)
            s=math.sin(2*math.pi*freq*t)+.2*math.sin(2*math.pi*freq*2.001*t)
        elif kind=='bass':
            env=min(t/.02,1)*math.exp(-t/2.2)*min((length-t)/.2,1)
            s=math.sin(2*math.pi*freq*t)+.3*math.sin(2*math.pi*freq*.5*t)
        else:
            env=min(t/.008,1)*math.exp(-t/1.1)*min((length-t)/.15,1)
            s=math.sin(2*math.pi*freq*t)+.18*math.sin(2*math.pi*freq*2*t)*math.exp(-t/.8)
        v=s*env*gain
        k=(int(start*RATE)+i)%N
        left[k]+=v*math.sqrt(1-pan); right[k]+=v*math.sqrt(pan)
bar=8.0
for c in range(8):
    chord=chords[c%4]
    root=chord[0]
    for midi in chord: add(c*bar, bar+2, midi, .015, .3, 'pad')
    for e in range(16):
        add(c*bar+e*.5, .5, root-12, .06, .5, 'bass')
        if e%2==1: add(c*bar+e*.5, .5, root, .035, .5, 'bass')
    arp=[chord[1],chord[2],chord[3],chord[2]+12]
    for e in range(8):
        add(c*bar+e*1.0, 1.2, arp[e%4]+12, .05, .3+(e%2)*.4, 'pluck')
peak=max(max(abs(v) for v in left),max(abs(v) for v in right))
scale=.68/max(peak,.001)
out=Path('Assets/StoryCycling/Resources/CampsBayRide.wav')
with wave.open(str(out),'wb') as w:
    w.setparams((2,2,RATE,0,'NONE','not compressed'))
    samples=array('h')
    for i in range(N): samples.extend((int(left[i]*scale*32767),int(right[i]*scale*32767)))
    w.writeframes(samples.tobytes())
print(f'Ride loop: {SECONDS}s, {out}')

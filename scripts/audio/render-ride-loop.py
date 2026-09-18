"""Original 'Camps Bay Ride' score v3: melodic, warm, upbeat. No samples.
64-second seamless Am/F/C/G progression with a pentatonic lead, soft kick and warm bass.
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
# A minor pentatonic lead tones (relative to A=57): A C D E G
lead=[69,72,74,76,79,81,84]
def add(start,length,midi,gain,pan,kind='pad'):
    freq=440*2**((midi-69)/12)
    count=int(length*RATE)
    for i in range(count):
        t=i/RATE
        if kind=='pad':
            env=min(t/1.4,1)*min((length-t)/2.8,1)
            s=math.sin(2*math.pi*freq*t)+.16*math.sin(2*math.pi*freq*2.001*t)+.08*math.sin(2*math.pi*freq*.5*t)
        elif kind=='bass':
            env=min(t/.02,1)*math.exp(-t/1.4)*min((length-t)/.15,1)
            s=math.sin(2*math.pi*freq*t)+.28*math.sin(2*math.pi*freq*.5*t)
        elif kind=='kick':
            env=math.exp(-t/0.14)
            s=math.sin(2*math.pi*(52-120*t)*t)
        elif kind=='lead':
            env=min(t/.03,1)*math.exp(-t/2.2)*min((length-t)/.35,1)
            vibrato=1+.006*math.sin(2*math.pi*5*t)
            s=math.sin(2*math.pi*freq*vibrato*t)+.12*math.sin(2*math.pi*freq*2*t)*math.exp(-t/1.2)
        v=s*env*gain
        k=(int(start*RATE)+i)%N
        left[k]+=v*math.sqrt(1-pan); right[k]+=v*math.sqrt(pan)
bar=8.0
melody=[4,2,3,5,4,2,1,3]  # lead indices per bar
for c in range(8):
    chord=chords[c%4]
    root=chord[0]
    for midi in chord: add(c*bar, bar+2, midi, .014, .3, 'pad')
    for b in range(4):
        add(c*bar+b*2.0, .35, 36, .20, .5, 'kick')
    for e in range(8):
        add(c*bar+e*1.0, 1.0, root-12, .035, .5, 'bass')
    # pentatonic lead phrase, resolving across the bar
    note = lead[melody[c%len(melody)]]
    add(c*bar+0.0, 3.0, note, .045, .42, 'lead')
    add(c*bar+3.5, 2.5, note-2, .035, .58, 'lead')
    add(c*bar+6.0, 1.8, note-3, .03, .5, 'lead')
peak=max(max(abs(v) for v in left),max(abs(v) for v in right))
scale=.68/max(peak,.001)
out=Path('Assets/StoryCycling/Resources/CampsBayRide.wav')
with wave.open(str(out),'wb') as w:
    w.setparams((2,2,RATE,0,'NONE','not compressed'))
    samples=array('h')
    for i in range(N): samples.extend((int(left[i]*scale*32767),int(right[i]*scale*32767)))
    w.writeframes(samples.tobytes())
print(f'Ride loop v3: {SECONDS}s, {out}')

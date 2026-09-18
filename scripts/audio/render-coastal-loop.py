"""Original 'Camps Bay Evening' score: no samples or third-party recordings.
64-second seamless Cmaj9/Am9/Fmaj9/Gsus ambient composition, stereo PCM.
All notes wrap around the loop; deterministic, regeneratable source.
"""
import math, wave, struct
from array import array
from pathlib import Path
RATE=22050
SECONDS=64
N=RATE*SECONDS
left=array('f',[0])*N
right=array('f',[0])*N
chords=[[48,55,59,62,64],[45,52,55,59,60],[41,48,52,55,57],[43,50,53,57,62]]
def note(start,length,midi,gain,pan,pad=False):
    freq=440*2**((midi-69)/12)
    count=int(length*RATE)
    for i in range(count):
        t=i/RATE
        if pad:
            env=min(t/2.5,1)*min((length-t)/4,1)
            sample=math.sin(2*math.pi*freq*t)+.16*math.sin(2*math.pi*freq*2.001*t)
        else:
            env=min(t/.025,1)*math.exp(-t/1.7)*min((length-t)/.3,1)
            sample=math.sin(2*math.pi*freq*t)+.22*math.sin(2*math.pi*freq*2*t)*math.exp(-t/1)
        value=sample*env*gain
        k=(int(start*RATE)+i)%N
        left[k]+=value*math.sqrt(1-pan);right[k]+=value*math.sqrt(pan)
for c,chord in enumerate(chords):
    for j,midi in enumerate(chord):note(c*16,21,midi,.018,.18+j*.15,True)
    melody=[chord[2]+12,chord[4]+12,chord[3]+12,chord[1]+12]
    for j,midi in enumerate(melody):
        note(c*16+2+j*3.5,6,midi,.055,.3+(j%2)*.4)
        note(c*16+2+j*3.5+.42,6,midi,.011,.7-(j%2)*.4)
# Smooth crest-to-trough swell, with no synthetic random hiss.
peak=max(max(abs(v) for v in left),max(abs(v) for v in right))
scale=.68/max(peak,.001)
out=Path('Assets/StoryCycling/Resources/CampsBayEvening.wav')
with wave.open(str(out),'wb') as w:
    w.setparams((2,2,RATE,0,'NONE','not compressed'))
    samples=array('h')
    for i in range(N):samples.extend((int(left[i]*scale*32767),int(right[i]*scale*32767)))
    w.writeframes(samples.tobytes())
print(f'Original loop: {SECONDS}s, peak normalized to -3.35 dBFS, {out}')

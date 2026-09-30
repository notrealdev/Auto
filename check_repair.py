new = open('game_new.bin', 'rb').read()
print(new[0x297D60:0x297D60+16].hex())

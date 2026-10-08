// Link stubs for octa.cpp symbols the probe never calls.
#include "engine/engine.h"
#include <cstdarg>

bool addcommand(const char*, identfun, const char*) { return true; }
void allchanged(bool) {}
void setsurface(cube&, int, const surfaceinfo&, const vertinfo*, int) {}
void reduceslope(ivec&) {}
void brightencube(cube&) {}
void renderprogress(float, const char*, GLuint, bool) {}
void freeoctaentities(cube&) {}
void conoutf(const char*, ...) {}
void conoutf(int, const char*, ...) {}
int totalmillis = 0;
int variable(const char*, int, int, int, int*, identfun, int) { return 0; }
void destroyva(vtxarray*, bool) {}
namespace game { void edittrigger(const selinfo&, int, int, int, int, const VSlot*) {} }
int worldsize = 0, worldscale = 0;
selinfo sel;

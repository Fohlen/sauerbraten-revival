// Link stubs for engine symbols the reference headers reference but the probe
// never calls (the probe links no engine .cpp — it only needs the geometry math
// and structs from the headers).
#include "engine/engine.h"
#include <cstdarg>

bool addcommand(const char*, identfun, const char*) { return true; }
void conoutf(const char*, ...) {}
void conoutf(int, const char*, ...) {}
int totalmillis = 0;
int variable(const char*, int, int, int, int*, identfun, int) { return 0; }

#include <cstdio>
#include <cmath>
#include <vector>
#include <string>

// Faithful extraction of Sauerbraten r6889 smoothmove and predictplayer logic
// Reference: src/fpsgame/client.cpp:1189-1207 and src/fpsgame/fps.cpp:172-193

struct vec
{
    float x, y, z;
    vec() : x(0), y(0), z(0) {}
    vec(float x, float y, float z) : x(x), y(y), z(z) {}

    vec &add(const vec &o) { x += o.x; y += o.y; z += o.z; return *this; }
    vec &sub(const vec &o) { x -= o.x; y -= o.y; z -= o.z; return *this; }
    vec &mul(float f) { x *= f; y *= f; z *= f; return *this; }
    float dist(const vec &o) const
    {
        float dx = x - o.x, dy = y - o.y, dz = z - o.z;
        return std::sqrt(dx*dx + dy*dy + dz*dz);
    }
};

struct fpsent
{
    vec o, vel, falling;
    vec deltapos, newpos;
    float yaw, pitch, roll;
    float deltayaw, deltapitch, deltaroll, newyaw, newpitch, newroll;
    int smoothmillis;
    int state;

    fpsent() : o(0, 0, 0), vel(0, 0, 0), falling(0, 0, 0),
               deltapos(0, 0, 0), newpos(0, 0, 0),
               yaw(0), pitch(0), roll(0),
               deltayaw(0), deltapitch(0), deltaroll(0),
               newyaw(0), newpitch(0), newroll(0),
               smoothmillis(-1), state(0)
    {}

    void resetinterp()
    {
        newpos = o;
        deltapos = vec(0, 0, 0);
    }
};

int smoothmove = 75;
int smoothdist = 32;

// From client.cpp:1189-1207
void on_remote_pos(fpsent *d, const vec &packet_pos, float packet_yaw, float packet_pitch, float packet_roll, int lastmillis)
{
    float oldyaw = d->yaw, oldpitch = d->pitch, oldroll = d->roll;
    d->yaw = packet_yaw;
    d->pitch = packet_pitch;
    d->roll = packet_roll;
    vec oldpos = d->o;
    d->o = packet_pos;

    if(smoothmove && d->smoothmillis >= 0 && oldpos.dist(d->o) < smoothdist)
    {
        d->newpos = d->o;
        d->newyaw = d->yaw;
        d->newpitch = d->pitch;
        d->newroll = d->roll;
        d->o = oldpos;
        d->yaw = oldyaw;
        d->pitch = oldpitch;
        d->roll = oldroll;
        (d->deltapos = oldpos).sub(d->newpos);
        d->deltayaw = oldyaw - d->newyaw;
        if(d->deltayaw > 180) d->deltayaw -= 360;
        else if(d->deltayaw < -180) d->deltayaw += 360;
        d->deltapitch = oldpitch - d->newpitch;
        d->deltaroll = oldroll - d->newroll;
        d->smoothmillis = lastmillis;
    }
    else
    {
        d->smoothmillis = 0;
    }
}

// From fps.cpp:172-193
void predictplayer(fpsent *d, bool move, int lastmillis, const vec *simulated_move_delta = nullptr)
{
    d->o = d->newpos;
    d->yaw = d->newyaw;
    d->pitch = d->newpitch;
    d->roll = d->newroll;
    if(move)
    {
        if(simulated_move_delta)
        {
            d->o.add(*simulated_move_delta);
        }
        d->newpos = d->o;
    }
    float k = 1.0f - float(lastmillis - d->smoothmillis) / smoothmove;
    if(k > 0)
    {
        d->o.add(vec(d->deltapos).mul(k));
        d->yaw += d->deltayaw * k;
        if(d->yaw < 0) d->yaw += 360;
        else if(d->yaw >= 360) d->yaw -= 360;
        d->pitch += d->deltapitch * k;
        d->roll += d->deltaroll * k;
    }
}

int main(int argc, char **argv)
{
    std::printf("{\n");
    std::printf("  \"smoothmove\": %d,\n", smoothmove);
    std::printf("  \"smoothdist\": %d,\n", smoothdist);

    // Scenario 1: Initial spawn (smoothmillis = -1) -> should set smoothmillis = 0, no smoothing
    {
        fpsent d;
        d.o = vec(0, 0, 0);
        d.yaw = 0;
        on_remote_pos(&d, vec(100, 200, 300), 45, 10, 0, 1000);
        std::printf("  \"initial_spawn\": {\n");
        std::printf("    \"smoothmillis\": %d,\n", d.smoothmillis);
        std::printf("    \"o\": [%.4f, %.4f, %.4f],\n", d.o.x, d.o.y, d.o.z);
        std::printf("    \"yaw\": %.4f\n", d.yaw);
        std::printf("  },\n");
    }

    // Scenario 2: Normal update within smoothdist at t=1000, followed by decay evaluation at 1000..1100 ms
    {
        fpsent d;
        // First packet establishes baseline
        on_remote_pos(&d, vec(100, 200, 300), 350, -10, 0, 967);
        // Second packet at 1000ms: moved to (105, 202, 301), yaw turned past 0 to 10
        on_remote_pos(&d, vec(105, 202, 301), 10, 5, 0, 1000);

        std::printf("  \"second_packet\": {\n");
        std::printf("    \"smoothmillis\": %d,\n", d.smoothmillis);
        std::printf("    \"deltapos\": [%.4f, %.4f, %.4f],\n", d.deltapos.x, d.deltapos.y, d.deltapos.z);
        std::printf("    \"deltayaw\": %.4f,\n", d.deltayaw);
        std::printf("    \"deltapitch\": %.4f,\n", d.deltapitch);
        std::printf("    \"newpos\": [%.4f, %.4f, %.4f],\n", d.newpos.x, d.newpos.y, d.newpos.z);
        std::printf("    \"newyaw\": %.4f,\n", d.newyaw);
        std::printf("    \"evaluations\": [\n");

        int eval_times[] = { 1000, 1015, 1030, 1045, 1060, 1075, 1090 };
        for(int i = 0; i < 7; ++i)
        {
            int t = eval_times[i];
            fpsent temp = d;
            predictplayer(&temp, false, t);
            float k = 1.0f - float(t - d.smoothmillis) / smoothmove;
            std::printf("      { \"time\": %d, \"k\": %.4f, \"o\": [%.4f, %.4f, %.4f], \"yaw\": %.4f, \"pitch\": %.4f }%s\n",
                t, k > 0 ? k : 0.0f, temp.o.x, temp.o.y, temp.o.z, temp.yaw, temp.pitch, i == 6 ? "" : ",");
        }
        std::printf("    ]\n");
        std::printf("  },\n");
    }

    // Scenario 3: Teleport or huge step exceeding smoothdist (>= 32)
    {
        fpsent d;
        on_remote_pos(&d, vec(100, 100, 100), 90, 0, 0, 500);
        // Distance = 50 >= 32
        on_remote_pos(&d, vec(150, 100, 100), 270, 0, 0, 533);
        std::printf("  \"teleport_exceeding_smoothdist\": {\n");
        std::printf("    \"smoothmillis\": %d,\n", d.smoothmillis);
        std::printf("    \"o\": [%.4f, %.4f, %.4f],\n", d.o.x, d.o.y, d.o.z);
        std::printf("    \"yaw\": %.4f\n", d.yaw);
        std::printf("  },\n");
    }

    // Scenario 4: Counter-clockwise yaw wrap (e.g. oldyaw = 5, newyaw = 355 -> shortest arc is -10 deg)
    {
        fpsent d;
        on_remote_pos(&d, vec(10, 10, 10), 5, 0, 0, 100);
        on_remote_pos(&d, vec(12, 10, 10), 355, 0, 0, 133);
        fpsent temp = d;
        predictplayer(&temp, false, 150);
        std::printf("  \"yaw_wrap_counter_clockwise\": {\n");
        std::printf("    \"deltayaw\": %.4f,\n", d.deltayaw);
        std::printf("    \"newyaw\": %.4f,\n", d.newyaw);
        std::printf("    \"predicted_yaw_at_150ms\": %.4f\n", temp.yaw);
        std::printf("  }\n");
    }

    std::printf("}\n");
    return 0;
}
